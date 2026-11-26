use std::borrow::Cow;
use std::collections::{BTreeMap, HashMap, HashSet};

use crate::sql_language::IdentifierQuoteStyle;
use crate::{QueryResult, TableKind, TableSchema};

pub const REFERENCE_LIST_LIMIT: usize = 200;
pub const REFERENCE_LOOKUP_LIMIT: usize = 25;
pub const MAX_INLINE_EDIT_CHARS: usize = 8_000;
pub const MAX_FORMAT_LENGTH: usize = 1_000_000;
pub const PREVIEW_CHAR_LIMIT: usize = 100_000;

/// SQL dialect classification used for literal formatting, DML batches, and FK lookup queries.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SqlDialectKind {
    TSql,
    Postgres,
    Sqlite,
    MySql,
    Generic,
}

impl SqlDialectKind {
    pub fn from_dialect_id(id: &str) -> Self {
        match id.trim().to_ascii_lowercase().as_str() {
            "mssql" | "tsql" | "sqlserver" => Self::TSql,
            "postgres" | "postgresql" | "pgsql" => Self::Postgres,
            "sqlite" => Self::Sqlite,
            "mysql" | "mariadb" => Self::MySql,
            _ => Self::Generic,
        }
    }

    pub fn quote_style(self) -> IdentifierQuoteStyle {
        match self {
            Self::TSql => IdentifierQuoteStyle::Brackets,
            Self::Postgres | Self::Sqlite | Self::Generic => IdentifierQuoteStyle::DoubleQuotes,
            Self::MySql => IdentifierQuoteStyle::Backticks,
        }
    }

    pub fn quote_ident(self, ident: &str) -> String {
        self.quote_style().quote(ident)
    }

    pub fn quote_table(self, schema: Option<&str>, table: &str) -> String {
        match schema.map(str::trim).filter(|s| !s.is_empty()) {
            Some(s) => format!("{}.{}", self.quote_ident(s), self.quote_ident(table)),
            None => self.quote_ident(table),
        }
    }
}

// ============================================================================
// 1. Copy Formatting & SQL Literals
// ============================================================================

/// Neutralize leading spreadsheet formula characters (`=`, `+`, `-`, `@`, `\t`, `\r`)
/// by prepending a single quote, matching WinUI's `ResultExport.NeutralizeSpreadsheetFormula`.
pub fn neutralize_spreadsheet_formula(value: &str) -> Cow<'_, str> {
    if let Some(first) = value.chars().next() {
        if matches!(first, '=' | '+' | '-' | '@' | '\t' | '\r') {
            let mut out = String::with_capacity(value.len() + 1);
            out.push('\'');
            out.push_str(value);
            return Cow::Owned(out);
        }
    }
    Cow::Borrowed(value)
}

/// Escape a single TSV field (formula neutralization + RFC quoting if it contains tabs/newlines/quotes).
pub fn escape_tsv_field(value: &str) -> String {
    let safe = neutralize_spreadsheet_formula(value);
    if safe.contains(['\t', '\n', '\r', '"']) {
        let mut out = String::with_capacity(safe.len() + 4);
        out.push('"');
        for ch in safe.chars() {
            if ch == '"' {
                out.push('"');
            }
            out.push(ch);
        }
        out.push('"');
        out
    } else {
        safe.into_owned()
    }
}

/// Format a selected block of cells as TSV (matching WinUI's `ResultsGrid.CopySelection`).
pub fn format_tsv_selection(
    headers: &[&str],
    rows: &[Vec<Option<&str>>],
    with_headers: bool,
) -> String {
    let mut out = String::new();
    let newline = if cfg!(windows) { "\r\n" } else { "\n" };

    if with_headers {
        for (i, h) in headers.iter().enumerate() {
            if i > 0 {
                out.push('\t');
            }
            out.push_str(&escape_tsv_field(h));
        }
        if !rows.is_empty() {
            out.push_str(newline);
        }
    }

    for (r_idx, row) in rows.iter().enumerate() {
        if r_idx > 0 {
            out.push_str(newline);
        }
        for (c_idx, cell) in row.iter().enumerate() {
            if c_idx > 0 {
                out.push('\t');
            }
            if let Some(val) = cell {
                out.push_str(&escape_tsv_field(val));
            }
        }
    }
    out
}

/// Normalize a column's declared data type into its lowercase base type name (before `(`).
pub fn base_data_type(data_type: Option<&str>) -> String {
    let raw = data_type.unwrap_or("").trim();
    let base = raw.split('(').next().unwrap_or(raw).trim();
    base.to_ascii_lowercase()
}

fn is_valid_sql_number(value: &str) -> bool {
    let trimmed = value.trim();
    if trimmed.is_empty() {
        return false;
    }
    trimmed.parse::<f64>().is_ok_and(|n| n.is_finite())
}

/// Format a cell value as a dialect-appropriate SQL literal, matching WinUI's
/// `TSqlDialect.FormatLiteral` / `PostgresDialect.FormatLiteral` / fallback.
pub fn format_sql_literal(
    dialect: SqlDialectKind,
    value: Option<&str>,
    data_type: Option<&str>,
) -> String {
    let Some(raw) = value else {
        return "NULL".to_string();
    };
    let ty = base_data_type(data_type);

    match dialect {
        SqlDialectKind::TSql => {
            if ty == "bit" || ty == "bitn" {
                if raw.eq_ignore_ascii_case("true") {
                    return "1".to_string();
                }
                if raw.eq_ignore_ascii_case("false") {
                    return "0".to_string();
                }
                if raw == "1" || raw == "0" {
                    return raw.to_string();
                }
            }
            if matches!(
                ty.as_str(),
                "tinyint"
                    | "smallint"
                    | "int"
                    | "int1"
                    | "int2"
                    | "int4"
                    | "int8"
                    | "intn"
                    | "bigint"
                    | "decimal"
                    | "decimaln"
                    | "numeric"
                    | "numericn"
                    | "money"
                    | "moneyn"
                    | "smallmoney"
                    | "float"
                    | "float4"
                    | "float8"
                    | "floatn"
                    | "real"
            ) && is_valid_sql_number(raw)
            {
                return raw.trim().to_string();
            }
            if matches!(
                ty.as_str(),
                "binary" | "varbinary" | "bigvarbin" | "bigbinary" | "image" | "timestamp" | "rowversion"
            ) {
                if raw.len() >= 2 && raw[..2].eq_ignore_ascii_case("0x") {
                    return raw.to_string();
                }
                return format!("0x{raw}");
            }
            if matches!(
                ty.as_str(),
                "datetime" | "datetime2" | "datetimen" | "smalldatetime" | "date" | "time" | "daten" | "timen"
            ) {
                let bytes = raw.as_bytes();
                if bytes.len() > 10 && bytes[4] == b'-' && bytes[7] == b'-' && bytes[10] == b' ' {
                    let mut iso = raw.to_string();
                    iso.replace_range(10..11, "T");
                    return format!("N'{}'", iso.replace('\'', "''"));
                }
            }
            format!("N'{}'", raw.replace('\'', "''"))
        }
        SqlDialectKind::Postgres => {
            if matches!(ty.as_str(), "boolean" | "bool") {
                return if raw.eq_ignore_ascii_case("true") || raw == "1" {
                    "TRUE".to_string()
                } else {
                    "FALSE".to_string()
                };
            }
            if matches!(
                ty.as_str(),
                "smallint"
                    | "int2"
                    | "integer"
                    | "int"
                    | "int4"
                    | "bigint"
                    | "int8"
                    | "decimal"
                    | "numeric"
                    | "real"
                    | "float4"
                    | "double precision"
                    | "float8"
                    | "money"
                    | "oid"
            ) && is_valid_sql_number(raw)
            {
                return raw.trim().to_string();
            }
            if ty == "bytea" && raw.len() >= 2 && raw[..2].eq_ignore_ascii_case("0x") {
                return format!("E'\\\\x{}'", &raw[2..]);
            }
            format!(
                "E'{}'",
                raw.replace('\\', "\\\\").replace('\'', "''")
            )
        }
        SqlDialectKind::Sqlite | SqlDialectKind::MySql | SqlDialectKind::Generic => {
            if ty.contains("binary") || ty.contains("image") {
                if raw.len() >= 2 && raw[..2].eq_ignore_ascii_case("0x") {
                    return raw.to_string();
                }
                return format!("'{}'", raw.replace('\'', "''"));
            }
            if matches!(ty.as_str(), "bit" | "boolean" | "bool") {
                if raw == "1" || raw == "0" {
                    return raw.to_string();
                }
                if raw.eq_ignore_ascii_case("true") {
                    return "1".to_string();
                }
                if raw.eq_ignore_ascii_case("false") {
                    return "0".to_string();
                }
            }
            if (ty.contains("int")
                || ty.contains("decimal")
                || ty.contains("numeric")
                || ty.contains("float")
                || ty.contains("real")
                || ty.contains("money")
                || ty.contains("double"))
                && is_valid_sql_number(raw)
            {
                return raw.trim().to_string();
            }
            if ty.contains("nvarchar") || ty.contains("nchar") || ty.contains("ntext") {
                return format!("N'{}'", raw.replace('\'', "''"));
            }
            format!("'{}'", raw.replace('\'', "''"))
        }
    }
}

/// Format selected cells as a SQL `IN (...)` clause (matching WinUI's `CopyAsSql(insert: false)`).
pub fn format_in_clause(dialect: SqlDialectKind, cells: &[(Option<&str>, Option<&str>)]) -> String {
    let mut out = String::from("IN (");
    for (i, &(val, ty)) in cells.iter().enumerate() {
        if i > 0 {
            out.push_str(", ");
        }
        out.push_str(&format_sql_literal(dialect, val, ty));
    }
    out.push(')');
    out
}

/// Format selected rows as a SQL `VALUES (...),\n       (...)` clause
/// (matching WinUI's `CopyAsSql(insert: true)`).
pub fn format_values_clause(
    dialect: SqlDialectKind,
    rows: &[Vec<(Option<&str>, Option<&str>)>],
) -> String {
    let mut out = String::from("VALUES ");
    for (r_idx, row) in rows.iter().enumerate() {
        if r_idx > 0 {
            out.push_str(",\n       ");
        }
        out.push('(');
        for (c_idx, &(val, ty)) in row.iter().enumerate() {
            if c_idx > 0 {
                out.push_str(", ");
            }
            out.push_str(&format_sql_literal(dialect, val, ty));
        }
        out.push(')');
    }
    out
}

// ============================================================================
// 2. Lexical SQL Scanner, Column-Origin Resolution, Row Edit & FK Resolution
// ============================================================================

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ColumnOrigin {
    pub schema: String,
    pub table: String,
    pub column: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum SqlTokKind {
    Word(String),
    QuotedIdent(String),
    StringLit,
    Number(String),
    Dot,
    Comma,
    Star,
    LParen,
    RParen,
    Semicolon,
    Other(char),
}

#[derive(Debug, Clone)]
struct SqlTok {
    kind: SqlTokKind,
    depth: usize,
}

fn tokenize_sql(sql: &str) -> Vec<SqlTok> {
    let bytes = sql.as_bytes();
    let len = bytes.len();
    let mut i = 0usize;
    let mut depth = 0usize;
    let mut out = Vec::new();

    while i < len {
        let b = bytes[i];
        if b.is_ascii_whitespace() {
            i += 1;
            continue;
        }
        // Line comment --
        if b == b'-' && i + 1 < len && bytes[i + 1] == b'-' {
            i += 2;
            while i < len && bytes[i] != b'\n' {
                i += 1;
            }
            continue;
        }
        // Block comment /* ... */
        if b == b'/' && i + 1 < len && bytes[i + 1] == b'*' {
            i += 2;
            while i + 1 < len && !(bytes[i] == b'*' && bytes[i + 1] == b'/') {
                i += 1;
            }
            i = (i + 2).min(len);
            continue;
        }
        // String literal '...' or N'...'
        if b == b'\'' || ((b == b'N' || b == b'n') && i + 1 < len && bytes[i + 1] == b'\'') {
            if b != b'\'' {
                i += 1;
            }
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
            out.push(SqlTok {
                kind: SqlTokKind::StringLit,
                depth,
            });
            continue;
        }
        // Bracketed identifier [...]
        if b == b'[' {
            i += 1;
            let mut ident = String::new();
            while i < len {
                if bytes[i] == b']' {
                    if i + 1 < len && bytes[i + 1] == b']' {
                        ident.push(']');
                        i += 2;
                        continue;
                    }
                    i += 1;
                    break;
                }
                let ch = sql[i..].chars().next().unwrap();
                ident.push(ch);
                i += ch.len_utf8();
            }
            out.push(SqlTok {
                kind: SqlTokKind::QuotedIdent(ident),
                depth,
            });
            continue;
        }
        // Double-quoted or backtick identifier
        if b == b'"' || b == b'`' {
            let q = b;
            i += 1;
            let mut ident = String::new();
            while i < len {
                if bytes[i] == q {
                    if i + 1 < len && bytes[i + 1] == q {
                        ident.push(q as char);
                        i += 2;
                        continue;
                    }
                    i += 1;
                    break;
                }
                let ch = sql[i..].chars().next().unwrap();
                ident.push(ch);
                i += ch.len_utf8();
            }
            out.push(SqlTok {
                kind: SqlTokKind::QuotedIdent(ident),
                depth,
            });
            continue;
        }
        match b {
            b'.' => {
                out.push(SqlTok { kind: SqlTokKind::Dot, depth });
                i += 1;
            }
            b',' => {
                out.push(SqlTok { kind: SqlTokKind::Comma, depth });
                i += 1;
            }
            b'*' => {
                out.push(SqlTok { kind: SqlTokKind::Star, depth });
                i += 1;
            }
            b';' => {
                out.push(SqlTok { kind: SqlTokKind::Semicolon, depth });
                i += 1;
            }
            b'(' => {
                out.push(SqlTok { kind: SqlTokKind::LParen, depth });
                depth += 1;
                i += 1;
            }
            b')' => {
                depth = depth.saturating_sub(1);
                out.push(SqlTok { kind: SqlTokKind::RParen, depth });
                i += 1;
            }
            b'0'..=b'9' => {
                let start = i;
                while i < len && (bytes[i].is_ascii_digit() || bytes[i] == b'.') {
                    i += 1;
                }
                out.push(SqlTok {
                    kind: SqlTokKind::Number(sql[start..i].to_string()),
                    depth,
                });
            }
            _ => {
                let ch = sql[i..].chars().next().unwrap();
                if ch.is_alphabetic() || ch == '_' || ch == '@' || ch == '#' {
                    let start = i;
                    i += ch.len_utf8();
                    while i < len {
                        let c = sql[i..].chars().next().unwrap();
                        if c.is_alphanumeric() || c == '_' || c == '$' || c == '#' {
                            i += c.len_utf8();
                        } else {
                            break;
                        }
                    }
                    out.push(SqlTok {
                        kind: SqlTokKind::Word(sql[start..i].to_string()),
                        depth,
                    });
                } else {
                    out.push(SqlTok {
                        kind: SqlTokKind::Other(ch),
                        depth,
                    });
                    i += ch.len_utf8();
                }
            }
        }
    }
    out
}

fn tok_ident(tok: &SqlTok) -> Option<&str> {
    match &tok.kind {
        SqlTokKind::Word(s) | SqlTokKind::QuotedIdent(s) => Some(s.as_str()),
        _ => None,
    }
}

fn tok_is_word(tok: &SqlTok, kw: &str) -> bool {
    matches!(&tok.kind, SqlTokKind::Word(s) if s.eq_ignore_ascii_case(kw))
}

const NOT_ALIASES: &[&str] = &[
    "WHERE", "ON", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "JOIN", "ORDER", "GROUP",
    "HAVING", "UNION", "INTERSECT", "EXCEPT", "SET", "VALUES", "INTO", "TOP", "WITH", "LIMIT",
    "OFFSET", "FETCH", "FOR", "OPTION", "AND", "OR", "WHEN", "THEN", "ELSE", "END", "FROM",
    "SELECT", "AS",
];

#[derive(Debug, Clone)]
struct TableRef<'a> {
    table: &'a TableSchema,
    alias: Option<String>,
}

fn parse_ident_path(tokens: &[SqlTok], start: usize) -> Option<(Vec<String>, usize)> {
    let first = tok_ident(tokens.get(start)?)?.to_string();
    let mut parts = vec![first];
    let mut idx = start + 1;
    while idx + 1 < tokens.len()
        && matches!(tokens[idx].kind, SqlTokKind::Dot)
        && tokens[idx].depth == tokens[start].depth
    {
        if let Some(next_part) = tok_ident(&tokens[idx + 1]) {
            parts.push(next_part.to_string());
            idx += 2;
        } else {
            break;
        }
    }
    Some((parts, idx))
}

fn find_schema_table<'a>(
    tables: &'a [TableSchema],
    parts: &[String],
) -> Option<&'a TableSchema> {
    match parts {
        [] => None,
        [name] => tables.iter().find(|t| t.name.eq_ignore_ascii_case(name)),
        [.., schema, name] => tables
            .iter()
            .find(|t| t.schema.eq_ignore_ascii_case(schema) && t.name.eq_ignore_ascii_case(name))
            .or_else(|| tables.iter().find(|t| t.name.eq_ignore_ascii_case(name))),
    }
}

fn find_table_references<'a>(tokens: &[SqlTok], tables: &'a [TableSchema]) -> Vec<TableRef<'a>> {
    let mut refs = Vec::new();
    let mut i = 0;
    while i < tokens.len() {
        if tokens[i].depth == 0 && (tok_is_word(&tokens[i], "FROM") || tok_is_word(&tokens[i], "JOIN")) {
            let mut cur = i + 1;
            // Parse comma-separated tables after FROM, or single table after JOIN.
            let is_from = tok_is_word(&tokens[i], "FROM");
            loop {
                if cur >= tokens.len() || tokens[cur].depth != 0 {
                    break;
                }
                if matches!(tokens[cur].kind, SqlTokKind::LParen) {
                    break;
                }
                let Some((parts, mut next)) = parse_ident_path(tokens, cur) else {
                    break;
                };
                let mut alias = None;
                if next < tokens.len() && tokens[next].depth == 0 {
                    if tok_is_word(&tokens[next], "AS") {
                        if let Some(a) = tokens.get(next + 1).and_then(tok_ident) {
                            alias = Some(a.to_string());
                            next += 2;
                        }
                    } else if let Some(a) = tok_ident(&tokens[next]) {
                        let is_kw = matches!(tokens[next].kind, SqlTokKind::Word(_))
                            && NOT_ALIASES.iter().any(|k| k.eq_ignore_ascii_case(a));
                        if !is_kw {
                            alias = Some(a.to_string());
                            next += 1;
                        }
                    }
                }
                if let Some(table) = find_schema_table(tables, &parts) {
                    refs.push(TableRef { table, alias });
                }
                // Skip optional table hints like WITH (NOLOCK)
                if next + 1 < tokens.len()
                    && tokens[next].depth == 0
                    && tok_is_word(&tokens[next], "WITH")
                    && matches!(tokens[next + 1].kind, SqlTokKind::LParen)
                {
                    next += 2;
                    while next < tokens.len() && tokens[next].depth > 0 {
                        next += 1;
                    }
                    if next < tokens.len() && matches!(tokens[next].kind, SqlTokKind::RParen) {
                        next += 1;
                    }
                }
                if is_from && next < tokens.len() && tokens[next].depth == 0 && matches!(tokens[next].kind, SqlTokKind::Comma) {
                    cur = next + 1;
                    continue;
                }
                i = next.saturating_sub(1);
                break;
            }
        }
        i += 1;
    }
    refs
}

#[derive(Debug, Clone)]
struct SelectProjection {
    qualifier: Option<String>,
    column: String,
}

fn find_top_level_select_slice(tokens: &[SqlTok]) -> Option<&[SqlTok]> {
    let sel_idx = tokens
        .iter()
        .position(|t| t.depth == 0 && tok_is_word(t, "SELECT"))?;
    let from_rel = tokens[sel_idx + 1..]
        .iter()
        .position(|t| t.depth == 0 && tok_is_word(t, "FROM"))?;
    let mut start = sel_idx + 1;
    let end = sel_idx + 1 + from_rel;

    // Skip DISTINCT / ALL
    while start < end
        && tokens[start].depth == 0
        && (tok_is_word(&tokens[start], "DISTINCT") || tok_is_word(&tokens[start], "ALL"))
    {
        start += 1;
    }
    // Skip TOP [(N) | N] [PERCENT] [WITH TIES]
    if start < end && tokens[start].depth == 0 && tok_is_word(&tokens[start], "TOP") {
        start += 1;
        if start < end && matches!(tokens[start].kind, SqlTokKind::LParen) {
            start += 1;
            while start < end && tokens[start].depth > 0 {
                start += 1;
            }
            if start < end && matches!(tokens[start].kind, SqlTokKind::RParen) {
                start += 1;
            }
        } else if start < end && matches!(tokens[start].kind, SqlTokKind::Number(_)) {
            start += 1;
        }
        if start < end && tok_is_word(&tokens[start], "PERCENT") {
            start += 1;
        }
        if start + 1 < end
            && tok_is_word(&tokens[start], "WITH")
            && tok_is_word(&tokens[start + 1], "TIES")
        {
            start += 2;
        }
    }

    if start >= end {
        return None;
    }
    Some(&tokens[start..end])
}

fn split_top_level_commas(slice: &[SqlTok]) -> Vec<&[SqlTok]> {
    let mut items = Vec::new();
    let mut start = 0usize;
    for (idx, tok) in slice.iter().enumerate() {
        if tok.depth == 0 && matches!(tok.kind, SqlTokKind::Comma) {
            items.push(&slice[start..idx]);
            start = idx + 1;
        }
    }
    if start <= slice.len() {
        items.push(&slice[start..]);
    }
    items
}

/// Parses SELECT projections. Returns `None` if `*` or `t.*` is present (star projection).
/// Otherwise returns one `Option<SelectProjection>` per item (`None` for computed expressions).
fn parse_select_projections(tokens: &[SqlTok]) -> Option<Vec<Option<SelectProjection>>> {
    let slice = find_top_level_select_slice(tokens)?;
    let raw_items = split_top_level_commas(slice);
    let mut result = Vec::with_capacity(raw_items.len());

    for item in raw_items {
        if item.is_empty() {
            return None;
        }
        // Star projection `*` or `t.*` or `s.t.*`
        if item.iter().any(|t| t.depth == 0 && matches!(t.kind, SqlTokKind::Star)) {
            return None;
        }
        // Simple projection: `<ident_path> [AS <alias> | <alias>]`
        let Some((parts, next)) = parse_ident_path(item, 0) else {
            result.push(None);
            continue;
        };
        let is_simple_tail = next == item.len()
            || (next + 1 == item.len() && tok_ident(&item[next]).is_some())
            || (next + 2 == item.len()
                && tok_is_word(&item[next], "AS")
                && tok_ident(&item[next + 1]).is_some());
        if !is_simple_tail {
            result.push(None);
            continue;
        }
        let proj = match parts.as_slice() {
            [col] => Some(SelectProjection {
                qualifier: None,
                column: col.clone(),
            }),
            [qual, col] => Some(SelectProjection {
                qualifier: Some(qual.clone()),
                column: col.clone(),
            }),
            [schema, table, col] => Some(SelectProjection {
                qualifier: Some(format!("{schema}.{table}")),
                column: col.clone(),
            }),
            _ => None,
        };
        result.push(proj);
    }

    Some(result)
}

fn has_multiple_statements(tokens: &[SqlTok]) -> bool {
    // Strip trailing semicolons; any remaining top-level semicolon means multi-statement batch.
    let mut end = tokens.len();
    while end > 0 && matches!(tokens[end - 1].kind, SqlTokKind::Semicolon) {
        end -= 1;
    }
    tokens[..end]
        .iter()
        .any(|t| t.depth == 0 && matches!(t.kind, SqlTokKind::Semicolon))
}

fn resolve_projection_table<'a>(
    qualifier: &str,
    refs: &[TableRef<'a>],
    tables: &'a [TableSchema],
) -> Option<&'a TableSchema> {
    if let Some((sch, tbl)) = qualifier.split_once('.') {
        if let Some(r) = refs.iter().find(|r| {
            r.table.schema.eq_ignore_ascii_case(sch) && r.table.name.eq_ignore_ascii_case(tbl)
        }) {
            return Some(r.table);
        }
        return tables
            .iter()
            .find(|t| t.schema.eq_ignore_ascii_case(sch) && t.name.eq_ignore_ascii_case(tbl));
    }
    // Match alias first, then table name in refs, then table name in full schema.
    refs.iter()
        .find(|r| r.alias.as_deref().is_some_and(|a| a.eq_ignore_ascii_case(qualifier)))
        .or_else(|| refs.iter().find(|r| r.table.name.eq_ignore_ascii_case(qualifier)))
        .map(|r| r.table)
        .or_else(|| tables.iter().find(|t| t.name.eq_ignore_ascii_case(qualifier)))
}

fn find_unique_owner<'a>(refs: &[TableRef<'a>], col_name: &str) -> Option<&'a TableSchema> {
    let mut unique: Vec<&TableSchema> = Vec::new();
    for r in refs {
        if !unique
            .iter()
            .any(|t| t.schema.eq_ignore_ascii_case(&r.table.schema) && t.name.eq_ignore_ascii_case(&r.table.name))
        {
            if r.table
                .columns
                .iter()
                .any(|c| c.name.eq_ignore_ascii_case(col_name))
            {
                unique.push(r.table);
            }
        }
    }
    if unique.len() == 1 {
        Some(unique[0])
    } else {
        None
    }
}

/// Map each result column index (`0..result.columns.len()`) to its source table & column
/// using the SQL statement and active database schema (matching WinUI's `CellReferenceResolver`).
pub fn resolve_column_origins(
    statement: Option<&str>,
    result: &QueryResult,
    tables: &[TableSchema],
) -> HashMap<usize, ColumnOrigin> {
    let mut origins = HashMap::new();
    let Some(stmt) = statement.map(str::trim).filter(|s| !s.is_empty()) else {
        return origins;
    };
    if result.columns.is_empty() || tables.is_empty() {
        return origins;
    }

    let tokens = tokenize_sql(stmt);
    if tokens.is_empty() || has_multiple_statements(&tokens) {
        return origins;
    }

    let refs = find_table_references(&tokens, tables);
    if refs.is_empty() {
        return origins;
    }

    let projections = parse_select_projections(&tokens);
    let use_projections = projections
        .as_ref()
        .filter(|p| p.len() == result.columns.len());

    for (idx, col) in result.columns.iter().enumerate() {
        let (table_opt, source_col) = if let Some(projs) = use_projections {
            match &projs[idx] {
                Some(proj) => {
                    let tbl = proj
                        .qualifier
                        .as_deref()
                        .and_then(|q| resolve_projection_table(q, &refs, tables))
                        .or_else(|| find_unique_owner(&refs, &proj.column));
                    (tbl, proj.column.clone())
                }
                None => continue, // Computed/expression column
            }
        } else {
            let tbl = find_unique_owner(&refs, &col.name);
            (tbl, col.name.clone())
        };

        if let Some(table) = table_opt {
            if let Some(schema_col) = table
                .columns
                .iter()
                .find(|c| c.name.eq_ignore_ascii_case(&source_col))
            {
                origins.insert(
                    idx,
                    ColumnOrigin {
                        schema: table.schema.clone(),
                        table: table.name.clone(),
                        column: schema_col.name.clone(),
                    },
                );
            }
        }
    }

    origins
}

// ============================================================================
// 3. Row Editability Validation & DML Planner (WinUI RowEditResolver parity)
// ============================================================================

const FORBIDDEN_SHAPE_WORDS: &[&str] = &[
    "GROUP", "HAVING", "DISTINCT", "UNION", "INTERSECT", "EXCEPT", "PIVOT", "UNPIVOT", "APPLY",
    "INTO", "OVER",
];

const AGGREGATE_WORDS: &[&str] = &[
    "COUNT", "SUM", "AVG", "MIN", "MAX", "STRING_AGG", "GROUPING", "STDEV", "VAR", "ARRAY_AGG",
    "JSON_AGG", "JSONB_AGG", "BOOL_AND", "BOOL_OR",
];

const LOSSY_KEY_TYPES: &[&str] = &[
    "datetime",
    "datetime2",
    "datetimen",
    "datetimeoffset",
    "time",
    "timen",
];

#[derive(Debug, Clone)]
pub struct RowKeyColumn {
    pub column: String,
    pub result_index: usize,
    pub data_type: Option<String>,
}

#[derive(Debug, Clone)]
pub struct RowEditTarget {
    pub schema: String,
    pub table: String,
    pub key: Vec<RowKeyColumn>,
    /// Map of result column index -> table column name (writable columns only).
    pub writable_columns: HashMap<usize, String>,
}

#[derive(Debug, Clone, Default)]
pub struct RowEditResolution {
    pub targets: Vec<RowEditTarget>,
    /// Result column index -> index into `targets`.
    pub column_to_target: HashMap<usize, usize>,
    /// Per-column refusal reason (for non-writable columns).
    pub column_reasons: HashMap<usize, String>,
    /// Global refusal reason when `targets` is empty.
    pub global_reason: Option<String>,
}

impl RowEditResolution {
    pub fn has_editable_targets(&self) -> bool {
        !self.targets.is_empty()
    }

    pub fn can_edit(&self, result_col_ix: usize) -> bool {
        self.column_to_target.contains_key(&result_col_ix)
    }

    pub fn target_for_column(&self, result_col_ix: usize) -> Option<&RowEditTarget> {
        self.column_to_target
            .get(&result_col_ix)
            .and_then(|&t| self.targets.get(t))
    }

    pub fn refuse_reason(&self, result_col_ix: usize) -> String {
        if let Some(reason) = self.column_reasons.get(&result_col_ix) {
            return reason.clone();
        }
        if let Some(global) = self.global_reason.as_ref() {
            return global.clone();
        }
        "This column can't be edited.".to_string()
    }
}

fn has_non_editable_shape(tokens: &[SqlTok]) -> bool {
    // 1. Forbidden top-level keywords
    for tok in tokens {
        if tok.depth == 0 {
            if let SqlTokKind::Word(w) = &tok.kind {
                if FORBIDDEN_SHAPE_WORDS.iter().any(|k| k.eq_ignore_ascii_case(w)) {
                    return true;
                }
            }
        }
    }
    // 2. Derived tables: top-level FROM ( or JOIN (
    for i in 0..tokens.len().saturating_sub(1) {
        if tokens[i].depth == 0
            && (tok_is_word(&tokens[i], "FROM") || tok_is_word(&tokens[i], "JOIN"))
            && matches!(tokens[i + 1].kind, SqlTokKind::LParen)
        {
            return true;
        }
    }
    // 3. Aggregate functions anywhere at depth 0: AGG(...)
    for i in 0..tokens.len().saturating_sub(1) {
        if tokens[i].depth == 0 {
            if let SqlTokKind::Word(w) = &tokens[i].kind {
                if AGGREGATE_WORDS.iter().any(|a| a.eq_ignore_ascii_case(w))
                    && matches!(tokens[i + 1].kind, SqlTokKind::LParen)
                {
                    return true;
                }
            }
        }
    }
    // 4. SELECT list: every item must be `*`, `t.*`, or simple column reference (no expressions)
    let Some(select_slice) = find_top_level_select_slice(tokens) else {
        return true;
    };
    for item in split_top_level_commas(select_slice) {
        if item.is_empty() {
            return true;
        }
        if item.len() == 1 && matches!(item[0].kind, SqlTokKind::Star) {
            continue;
        }
        // Check `a.*` or `a.b.*`
        if matches!(item.last().map(|t| &t.kind), Some(SqlTokKind::Star)) {
            if item.len() >= 3
                && matches!(item[item.len() - 2].kind, SqlTokKind::Dot)
                && parse_ident_path(&item[..item.len() - 2], 0)
                    .is_some_and(|(_, next)| next == item.len() - 2)
            {
                continue;
            }
            return true;
        }
        let Some((_, next)) = parse_ident_path(item, 0) else {
            return true;
        };
        let valid_tail = next == item.len()
            || (next + 1 == item.len() && tok_ident(&item[next]).is_some())
            || (next + 2 == item.len()
                && tok_is_word(&item[next], "AS")
                && tok_ident(&item[next + 1]).is_some());
        if !valid_tail {
            return true;
        }
    }
    false
}

/// Resolve editability for a `QueryResult` using WinUI's strict `RowEditResolver` rules.
pub fn resolve_row_edit(
    statement: Option<&str>,
    result: &QueryResult,
    tables: &[TableSchema],
    origins: &HashMap<usize, ColumnOrigin>,
) -> RowEditResolution {
    if result.columns.is_empty() {
        return RowEditResolution {
            global_reason: Some("This result has no columns to edit.".to_string()),
            ..Default::default()
        };
    }

    let Some(stmt) = statement.map(str::trim).filter(|s| !s.is_empty()) else {
        return RowEditResolution {
            global_reason: Some(
                "Editing needs a plain SELECT from a table whose rows the result still identifies."
                    .to_string(),
            ),
            ..Default::default()
        };
    };

    if origins.is_empty() {
        return RowEditResolution {
            global_reason: Some(
                "Editing needs a plain SELECT from a table whose rows the result still identifies."
                    .to_string(),
            ),
            ..Default::default()
        };
    }

    let tokens = tokenize_sql(stmt);
    if has_multiple_statements(&tokens) || has_non_editable_shape(&tokens) {
        return RowEditResolution {
            global_reason: Some(
                "Editing is off for grouped, distinct, combined or computed queries. Run a plain SELECT and edit that."
                    .to_string(),
            ),
            ..Default::default()
        };
    }

    // Group mapped columns by `(schema_lower, table_lower)` in deterministic order.
    let mut groups: BTreeMap<(String, String), Vec<(usize, &ColumnOrigin)>> = BTreeMap::new();
    for (&col_idx, origin) in origins {
        groups
            .entry((
                origin.schema.to_ascii_lowercase(),
                origin.table.to_ascii_lowercase(),
            ))
            .or_default()
            .push((col_idx, origin));
    }

    let mut resolution = RowEditResolution::default();

    for ((sch_lo, tbl_lo), mut cols) in groups {
        cols.sort_by_key(|(idx, _)| *idx);
        let display_name = {
            let first = cols[0].1;
            if first.schema.is_empty() {
                first.table.clone()
            } else {
                format!("{}.{}", first.schema, first.table)
            }
        };

        let Some(schema_info) = tables.iter().find(|t| {
            t.schema.to_ascii_lowercase() == sch_lo && t.name.to_ascii_lowercase() == tbl_lo
        }) else {
            let msg = format!("The schema doesn't know {display_name} yet.");
            for &(idx, _) in &cols {
                resolution.column_reasons.insert(idx, msg.clone());
            }
            continue;
        };

        if schema_info.kind != TableKind::Table {
            let msg = format!("{display_name} is a view, so it has no row to update.");
            for &(idx, _) in &cols {
                resolution.column_reasons.insert(idx, msg.clone());
            }
            continue;
        }

        if schema_info.primary_key.is_empty() {
            let msg = format!("{display_name} has no primary key, so a row can't be identified.");
            for &(idx, _) in &cols {
                resolution.column_reasons.insert(idx, msg.clone());
            }
            continue;
        }

        // Verify every PK column appears exactly once and isn't a lossy date/time type.
        let mut keys: Vec<RowKeyColumn> = Vec::with_capacity(schema_info.primary_key.len());
        let mut missing_keys: Vec<String> = Vec::new();
        let mut key_error: Option<String> = None;

        for pk_col in &schema_info.primary_key {
            let matches: Vec<usize> = cols
                .iter()
                .filter(|(_, o)| o.column.eq_ignore_ascii_case(pk_col))
                .map(|(idx, _)| *idx)
                .collect();
            if matches.is_empty() {
                missing_keys.push(pk_col.clone());
                continue;
            }
            if matches.len() > 1 {
                key_error = Some(format!(
                    "The result shows the key of {display_name} more than once, so a row can't be identified."
                ));
                break;
            }
            let res_idx = matches[0];
            let col_type = schema_info
                .columns
                .iter()
                .find(|c| c.name.eq_ignore_ascii_case(pk_col))
                .and_then(|c| c.data_type.clone())
                .or_else(|| result.columns.get(res_idx).and_then(|c| c.data_type.clone()));
            let base_ty = base_data_type(col_type.as_deref());
            if LOSSY_KEY_TYPES.iter().any(|t| *t == base_ty) {
                key_error = Some(format!(
                    "{display_name}.{pk_col} is a date/time key shown without enough precision to match one row."
                ));
                break;
            }
            keys.push(RowKeyColumn {
                column: pk_col.clone(),
                result_index: res_idx,
                data_type: col_type,
            });
        }

        if let Some(err) = key_error {
            for &(idx, _) in &cols {
                resolution.column_reasons.insert(idx, err.clone());
            }
            continue;
        }
        if !missing_keys.is_empty() {
            let msg = format!(
                "The result doesn't include the primary key of {display_name} ({}).",
                missing_keys.join(", ")
            );
            for &(idx, _) in &cols {
                resolution.column_reasons.insert(idx, msg.clone());
            }
            continue;
        }

        let mut writable = HashMap::new();
        let mut used_cols: HashSet<String> = HashSet::new();

        for &(res_idx, origin) in &cols {
            let col_key = origin.column.to_ascii_lowercase();
            if !used_cols.insert(col_key) {
                resolution.column_reasons.insert(
                    res_idx,
                    format!(
                        "{display_name}.{} appears more than once; edit its first column.",
                        origin.column
                    ),
                );
                continue;
            }
            if let Some(detail) = schema_info
                .columns
                .iter()
                .find(|c| c.name.eq_ignore_ascii_case(&origin.column))
            {
                if detail.is_identity || detail.is_computed {
                    resolution.column_reasons.insert(
                        res_idx,
                        format!("{display_name}.{} is generated and can't be written.", origin.column),
                    );
                    continue;
                }
                let ty = base_data_type(detail.data_type.as_deref());
                if ty == "timestamp" || ty == "rowversion" {
                    resolution.column_reasons.insert(
                        res_idx,
                        format!(
                            "{display_name}.{} is a row version and can't be written.",
                            origin.column
                        ),
                    );
                    continue;
                }
            }
            writable.insert(res_idx, origin.column.clone());
        }

        if writable.is_empty() {
            let msg = format!(
                "Every column of {display_name} in this result is generated or read-only."
            );
            for &(idx, _) in &cols {
                resolution
                    .column_reasons
                    .entry(idx)
                    .or_insert_with(|| msg.clone());
            }
            continue;
        }

        let target_idx = resolution.targets.len();
        for &res_idx in writable.keys() {
            resolution.column_to_target.insert(res_idx, target_idx);
        }
        resolution.targets.push(RowEditTarget {
            schema: schema_info.schema.clone(),
            table: schema_info.name.clone(),
            key: keys,
            writable_columns: writable,
        });
    }

    if resolution.targets.is_empty() && resolution.global_reason.is_none() {
        resolution.global_reason = resolution
            .column_reasons
            .values()
            .next()
            .cloned()
            .or_else(|| Some("This result can't be edited.".to_string()));
    }

    resolution
}

#[derive(Debug, Clone)]
pub struct PlannedStatement {
    pub sql: String,
    pub is_delete: bool,
}

#[derive(Debug, Clone)]
pub struct RowEditPlan {
    pub statements: Vec<PlannedStatement>,
    pub batch_sql: String,
    pub update_count: usize,
    pub delete_count: usize,
}

fn build_key_predicate(
    dialect: SqlDialectKind,
    target: &RowEditTarget,
    original_row: &[Option<String>],
) -> Result<String, String> {
    let mut parts = Vec::with_capacity(target.key.len());
    for key in &target.key {
        let val = original_row
            .get(key.result_index)
            .and_then(|v| v.as_deref());
        let col_quoted = dialect.quote_ident(&key.column);
        match val {
            None => parts.push(format!("{col_quoted} IS NULL")),
            Some(v) => {
                let lit = format_sql_literal(dialect, Some(v), key.data_type.as_deref());
                parts.push(format!("{col_quoted} = {lit}"));
            }
        }
    }
    if parts.is_empty() {
        return Err("Row has no primary key predicate.".to_string());
    }
    Ok(parts.join(" AND "))
}

pub fn wrap_dml_batch(dialect: SqlDialectKind, statements: &[PlannedStatement]) -> String {
    match dialect {
        SqlDialectKind::TSql => {
            let mut out = String::from("BEGIN TRY\n    BEGIN TRANSACTION;\n");
            for stmt in statements {
                out.push_str("    ");
                out.push_str(&stmt.sql);
                out.push_str(";\n");
            }
            out.push_str(
                "    COMMIT TRANSACTION;\nEND TRY\nBEGIN CATCH\n    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;\n    THROW;\nEND CATCH",
            );
            out
        }
        _ => {
            let mut out = String::from("BEGIN;\n");
            for stmt in statements {
                out.push_str(&stmt.sql);
                out.push_str(";\n");
            }
            out.push_str("COMMIT;");
            out
        }
    }
}

/// Build an executable DML plan (UPDATEs first, DELETEs second, wrapped in a transaction)
/// from staged cell edits and deleted row indices, matching WinUI's `RowEditController.Plan`.
pub fn build_dml_plan(
    dialect: SqlDialectKind,
    resolution: &RowEditResolution,
    result: &QueryResult,
    tables: &[TableSchema],
    staged_edits: &HashMap<(usize, usize), Option<String>>,
    deleted_rows: &HashSet<usize>,
    focus_src_col: Option<usize>,
) -> Result<RowEditPlan, String> {
    if !resolution.has_editable_targets() {
        return Err(resolution
            .global_reason
            .clone()
            .unwrap_or_else(|| "This result can't be edited.".to_string()));
    }

    let mut statements: Vec<PlannedStatement> = Vec::new();
    let mut update_count = 0usize;
    let mut delete_count = 0usize;

    // 1. Group staged edits by ascending row_ix, skipping rows marked for deletion.
    let mut edits_by_row: BTreeMap<usize, Vec<(usize, &Option<String>)>> = BTreeMap::new();
    for (&(row_ix, src_col), new_val) in staged_edits {
        if deleted_rows.contains(&row_ix) {
            continue;
        }
        edits_by_row.entry(row_ix).or_default().push((src_col, new_val));
    }

    for (row_ix, mut row_edits) in edits_by_row {
        row_edits.sort_by_key(|(c, _)| *c);
        let Some(original_row) = result.rows.get(row_ix) else {
            return Err(format!("Row {} is no longer available.", row_ix + 1));
        };

        // Group edits by target table index so multi-table JOIN edits produce one UPDATE per table.
        let mut by_target: BTreeMap<usize, Vec<(usize, &Option<String>)>> = BTreeMap::new();
        for (src_col, new_val) in row_edits {
            let orig = original_row.get(src_col).and_then(|v| v.as_deref());
            if orig == new_val.as_deref() {
                continue;
            }
            let Some(&target_ix) = resolution.column_to_target.get(&src_col) else {
                return Err(resolution.refuse_reason(src_col));
            };
            by_target.entry(target_ix).or_default().push((src_col, new_val));
        }

        for (target_ix, col_edits) in by_target {
            let target = &resolution.targets[target_ix];
            let table_schema = tables.iter().find(|t| {
                t.schema.eq_ignore_ascii_case(&target.schema)
                    && t.name.eq_ignore_ascii_case(&target.table)
            });

            let mut assignments = Vec::with_capacity(col_edits.len());
            for (src_col, new_val) in col_edits {
                let col_name = target
                    .writable_columns
                    .get(&src_col)
                    .ok_or_else(|| resolution.refuse_reason(src_col))?;
                let col_type = table_schema
                    .and_then(|ts| {
                        ts.columns
                            .iter()
                            .find(|c| c.name.eq_ignore_ascii_case(col_name))
                            .and_then(|c| c.data_type.as_deref())
                    })
                    .or_else(|| result.columns.get(src_col).and_then(|c| c.data_type.as_deref()));
                let lit = format_sql_literal(dialect, new_val.as_deref(), col_type);
                assignments.push(format!("{} = {lit}", dialect.quote_ident(col_name)));
            }

            let where_clause = build_key_predicate(dialect, target, original_row)?;
            let qualified = dialect.quote_table(Some(&target.schema), &target.table);
            statements.push(PlannedStatement {
                sql: format!(
                    "UPDATE {qualified} SET {} WHERE {where_clause}",
                    assignments.join(", ")
                ),
                is_delete: false,
            });
            update_count += 1;
        }
    }

    // 2. Emit DELETE statements in ascending row order.
    let mut del_rows: Vec<usize> = deleted_rows.iter().copied().collect();
    del_rows.sort_unstable();

    for row_ix in del_rows {
        let Some(original_row) = result.rows.get(row_ix) else {
            return Err(format!("Row {} is no longer available.", row_ix + 1));
        };
        let target = focus_src_col
            .and_then(|c| resolution.target_for_column(c))
            .or_else(|| resolution.targets.first())
            .ok_or_else(|| "No editable table target found for delete.".to_string())?;
        let where_clause = build_key_predicate(dialect, target, original_row)?;
        let qualified = dialect.quote_table(Some(&target.schema), &target.table);
        statements.push(PlannedStatement {
            sql: format!("DELETE FROM {qualified} WHERE {where_clause}"),
            is_delete: true,
        });
        delete_count += 1;
    }

    if statements.is_empty() {
        return Err("No changes to save.".to_string());
    }

    let batch_sql = wrap_dml_batch(dialect, &statements);
    Ok(RowEditPlan {
        statements,
        batch_sql,
        update_count,
        delete_count,
    })
}

// ============================================================================
// 4. Foreign Key Reference Resolution & Query Builder
// ============================================================================

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct KeyColumnPair {
    pub source_column: String,
    pub target_column: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ColumnReference {
    pub constraint: String,
    pub source_schema: String,
    pub source_table: String,
    pub source_column: String,
    pub target_schema: Option<String>,
    pub target_table: String,
    pub target_column: String,
    pub columns: Vec<KeyColumnPair>,
}

impl ColumnReference {
    pub fn target_display(&self) -> String {
        match self.target_schema.as_deref().filter(|s| !s.is_empty()) {
            Some(sch) => format!("{sch}.{}", self.target_table),
            None => self.target_table.clone(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReferenceFilter {
    pub target_column: String,
    pub value: Option<String>,
    pub data_type: Option<String>,
}

/// Resolve DB foreign-key references for every mapped result column
/// (matching WinUI's `CellReferenceResolver.FindReference`).
pub fn resolve_column_references(
    origins: &HashMap<usize, ColumnOrigin>,
    tables: &[TableSchema],
) -> HashMap<usize, ColumnReference> {
    let mut out = HashMap::new();
    for (&res_col_ix, origin) in origins {
        let Some(table) = tables.iter().find(|t| {
            t.schema.eq_ignore_ascii_case(&origin.schema)
                && t.name.eq_ignore_ascii_case(&origin.table)
        }) else {
            continue;
        };

        // Group foreign keys by constraint name.
        let mut by_constraint: BTreeMap<String, Vec<&crate::ForeignKeySchema>> = BTreeMap::new();
        for fk in &table.foreign_keys {
            by_constraint
                .entry(fk.constraint.clone())
                .or_default()
                .push(fk);
        }

        // Filter to constraints containing `origin.column` and prefer fewest columns (single before composite).
        let mut matching: Vec<(String, Vec<&crate::ForeignKeySchema>)> = by_constraint
            .into_iter()
            .filter(|(_, fks)| {
                fks.iter()
                    .any(|fk| fk.column.eq_ignore_ascii_case(&origin.column))
            })
            .collect();
        matching.sort_by_key(|(name, fks)| (fks.len(), name.clone()));

        let Some((constraint, fks)) = matching.into_iter().next() else {
            continue;
        };
        let primary_fk = fks
            .iter()
            .find(|fk| fk.column.eq_ignore_ascii_case(&origin.column))
            .copied()
            .unwrap_or(fks[0]);

        let pairs: Vec<KeyColumnPair> = fks
            .iter()
            .map(|fk| KeyColumnPair {
                source_column: fk.column.clone(),
                target_column: fk.referenced_column.clone(),
            })
            .collect();

        out.insert(
            res_col_ix,
            ColumnReference {
                constraint,
                source_schema: table.schema.clone(),
                source_table: table.name.clone(),
                source_column: origin.column.clone(),
                target_schema: primary_fk.referenced_schema.clone(),
                target_table: primary_fk.referenced_table.clone(),
                target_column: primary_fk.referenced_column.clone(),
                columns: pairs,
            },
        );
    }
    out
}

/// Build target key filters from a row (using `display_value` callback so staged edits are honored).
/// Returns `(filters, optional_warning)`.
pub fn build_reference_filters(
    reference: &ColumnReference,
    result: &QueryResult,
    row_ix: usize,
    staged_edits: &HashMap<(usize, usize), Option<String>>,
) -> (Vec<ReferenceFilter>, Option<String>) {
    let mut filters = Vec::new();
    let mut skipped = 0usize;

    for pair in &reference.columns {
        if let Some((col_ix, col)) = result
            .columns
            .iter()
            .enumerate()
            .find(|(_, c)| c.name.eq_ignore_ascii_case(&pair.source_column))
        {
            let value = if let Some(staged) = staged_edits.get(&(row_ix, col_ix)) {
                staged.clone()
            } else {
                result
                    .rows
                    .get(row_ix)
                    .and_then(|r| r.get(col_ix))
                    .and_then(|v| v.clone())
            };
            filters.push(ReferenceFilter {
                target_column: pair.target_column.clone(),
                value,
                data_type: col.data_type.clone(),
            });
        } else {
            skipped += 1;
        }
    }

    let warning = if filters.is_empty() {
        Some(
            "The result set does not include the reference key column, so there is nothing to look up."
                .to_string(),
        )
    } else if skipped > 0 {
        Some(format!(
            "Composite key: only {} of {} key columns are in this result.",
            filters.len(),
            reference.columns.len()
        ))
    } else {
        None
    };

    (filters, warning)
}

#[derive(Debug, Clone)]
pub struct ReferenceQueryPlan {
    pub sql: String,
    pub is_list: bool,
    pub limit: usize,
}

/// Generate the SQL query for FK reference preview (`list` mode for <=200 estimated rows,
/// filtered `lookup` mode otherwise), matching WinUI's `ISqlDialect.RowsStatement`.
pub fn build_reference_query(
    dialect: SqlDialectKind,
    reference: &ColumnReference,
    filters: &[ReferenceFilter],
    estimated_rows: Option<i64>,
) -> ReferenceQueryPlan {
    let is_list = estimated_rows.is_some_and(|n| (0..=REFERENCE_LIST_LIMIT as i64).contains(&n));
    let limit = if is_list {
        REFERENCE_LIST_LIMIT
    } else {
        REFERENCE_LOOKUP_LIMIT
    };
    let table_sql = dialect.quote_table(reference.target_schema.as_deref(), &reference.target_table);

    let sql = if is_list {
        let order_col = dialect.quote_ident(&reference.target_column);
        match dialect {
            SqlDialectKind::TSql => {
                format!("SELECT TOP ({limit}) * FROM {table_sql} ORDER BY {order_col}")
            }
            _ => {
                format!("SELECT * FROM {table_sql} ORDER BY {order_col} LIMIT {limit}")
            }
        }
    } else {
        let predicates: Vec<String> = filters
            .iter()
            .map(|f| {
                let col = dialect.quote_ident(&f.target_column);
                match f.value.as_deref() {
                    None => format!("{col} IS NULL"),
                    Some(v) => {
                        let lit = format_sql_literal(dialect, Some(v), f.data_type.as_deref());
                        format!("{col} = {lit}")
                    }
                }
            })
            .collect();
        let where_sql = if predicates.is_empty() {
            String::new()
        } else {
            format!(" WHERE {}", predicates.join(" AND "))
        };
        match dialect {
            SqlDialectKind::TSql => format!("SELECT TOP ({limit}) * FROM {table_sql}{where_sql}"),
            _ => format!("SELECT * FROM {table_sql}{where_sql} LIMIT {limit}"),
        }
    };

    ReferenceQueryPlan {
        sql,
        is_list,
        limit,
    }
}

pub fn values_match(a: Option<&str>, b: Option<&str>) -> bool {
    match (a, b) {
        (None, None) => true,
        (Some(_), None) | (None, Some(_)) => false,
        (Some(x), Some(y)) => {
            let xt = x.trim();
            let yt = y.trim();
            if xt.eq_ignore_ascii_case(yt) {
                return true;
            }
            if let (Ok(nx), Ok(ny)) = (xt.parse::<f64>(), yt.parse::<f64>()) {
                return (nx - ny).abs() <= f64::EPSILON * nx.abs().max(ny.abs()).max(1.0);
            }
            false
        }
    }
}

/// Find row indices in the FK lookup result matching all `filters`.
pub fn find_matched_rows(result: &QueryResult, filters: &[ReferenceFilter]) -> Vec<usize> {
    if filters.is_empty() || result.rows.is_empty() {
        return Vec::new();
    }
    let mapped: Vec<(usize, &ReferenceFilter)> = filters
        .iter()
        .filter_map(|f| {
            result
                .columns
                .iter()
                .position(|c| c.name.eq_ignore_ascii_case(&f.target_column))
                .map(|idx| (idx, f))
        })
        .collect();
    if mapped.is_empty() {
        return Vec::new();
    }

    let mut matched = Vec::new();
    for (r_ix, row) in result.rows.iter().enumerate() {
        let all_match = mapped.iter().all(|&(c_ix, f)| {
            let cell = row.get(c_ix).and_then(|v| v.as_deref());
            values_match(cell, f.value.as_deref())
        });
        if all_match {
            matched.push(r_ix);
        }
    }
    matched
}

// ============================================================================
// 5. Cell Preview Formatting (Auto / JSON / XML / Plain)
// ============================================================================

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum CellFormat {
    #[default]
    Auto,
    Json,
    Xml,
    Plain,
}

impl CellFormat {
    pub fn label(self) -> &'static str {
        match self {
            Self::Auto => "Auto",
            Self::Json => "JSON",
            Self::Xml => "XML",
            Self::Plain => "Plain",
        }
    }
}

#[derive(Debug, Clone)]
pub struct FormattedCell {
    /// Full formatted (or raw) text for copying.
    pub full_text: String,
    /// Display-capped text (at `PREVIEW_CHAR_LIMIT` chars).
    pub display_text: String,
    pub detected: CellFormat,
    pub error_note: Option<String>,
    pub total_chars: usize,
    pub is_truncated: bool,
}

fn looks_like_xml(trimmed: &str) -> bool {
    if !trimmed.starts_with('<') || !trimmed.ends_with('>') {
        return false;
    }
    // Find first opening tag name `<tagName ...>`
    let bytes = trimmed.as_bytes();
    let mut i = 0usize;
    while i < bytes.len() {
        if bytes[i] == b'<' {
            let next = bytes.get(i + 1).copied();
            if matches!(next, Some(b'?') | Some(b'!')) {
                if let Some(close) = trimmed[i..].find('>') {
                    i += close + 1;
                    continue;
                }
                return false;
            }
            if next == Some(b'/') {
                return false;
            }
            let start = i + 1;
            let mut end = start;
            while end < bytes.len()
                && !bytes[end].is_ascii_whitespace()
                && bytes[end] != b'>'
                && bytes[end] != b'/'
            {
                end += 1;
            }
            if end == start {
                return false;
            }
            let tag = &trimmed[start..end];
            let closing = format!("</{tag}>");
            if trimmed.contains(&closing) {
                return true;
            }
            // Also allow self-closing single root tag `<tag .../>`
            if trimmed.ends_with("/>") {
                return true;
            }
            return false;
        }
        i += 1;
    }
    false
}

fn pretty_print_xml(raw: &str) -> String {
    // Insert newline between adjacent tags `>   <` -> `>\n<`
    let mut normalized = String::with_capacity(raw.len());
    let mut chars = raw.chars().peekable();
    while let Some(ch) = chars.next() {
        normalized.push(ch);
        if ch == '>' {
            let mut ws = String::new();
            while let Some(&next) = chars.peek() {
                if next.is_whitespace() {
                    ws.push(next);
                    chars.next();
                } else {
                    break;
                }
            }
            if chars.peek() == Some(&'<') {
                normalized.push('\n');
            } else {
                normalized.push_str(&ws);
            }
        }
    }

    let mut out = String::with_capacity(normalized.len() + 64);
    let mut indent = 0usize;
    for (idx, line) in normalized.lines().enumerate() {
        let t = line.trim();
        if t.is_empty() {
            continue;
        }
        if t.starts_with("</") {
            indent = indent.saturating_sub(1);
        }
        if idx > 0 && !out.is_empty() {
            out.push('\n');
        }
        for _ in 0..indent {
            out.push_str("  ");
        }
        out.push_str(t);

        let is_open = t.starts_with('<')
            && !t.starts_with("</")
            && !t.starts_with("<?")
            && !t.starts_with("<!")
            && !t.ends_with("/>")
            && !t.contains("</");
        if is_open {
            indent += 1;
        }
    }
    out
}

fn cap_display(full: &str) -> (String, usize, bool) {
    let mut count = 0usize;
    let mut cut = full.len();
    let mut truncated = false;
    for (byte_idx, _) in full.char_indices() {
        if count == PREVIEW_CHAR_LIMIT {
            cut = byte_idx;
            truncated = true;
            break;
        }
        count += 1;
    }
    if truncated {
        let total = count + full[cut..].chars().count();
        (full[..cut].to_string(), total, true)
    } else {
        (full.to_string(), count, false)
    }
}

/// Format a cell value for the preview pane, matching WinUI's `CellContentFormatter`.
pub fn format_cell_content(raw: &str, requested: CellFormat) -> FormattedCell {
    if raw.len() > MAX_FORMAT_LENGTH {
        let (display_text, total_chars, is_truncated) = cap_display(raw);
        let error_note = (requested != CellFormat::Auto && requested != CellFormat::Plain)
            .then(|| "Too large to format, showing raw text".to_string());
        return FormattedCell {
            full_text: raw.to_string(),
            display_text,
            detected: CellFormat::Plain,
            error_note,
            total_chars,
            is_truncated,
        };
    }

    let trimmed = raw.trim();
    match requested {
        CellFormat::Plain => {
            let (display_text, total_chars, is_truncated) = cap_display(raw);
            FormattedCell {
                full_text: raw.to_string(),
                display_text,
                detected: CellFormat::Plain,
                error_note: None,
                total_chars,
                is_truncated,
            }
        }
        CellFormat::Json => match serde_json::from_str::<serde_json::Value>(trimmed) {
            Ok(val) => {
                let pretty = serde_json::to_string_pretty(&val).unwrap_or_else(|_| raw.to_string());
                let (display_text, total_chars, is_truncated) = cap_display(&pretty);
                FormattedCell {
                    full_text: pretty,
                    display_text,
                    detected: CellFormat::Json,
                    error_note: None,
                    total_chars,
                    is_truncated,
                }
            }
            Err(_) => {
                let (display_text, total_chars, is_truncated) = cap_display(raw);
                FormattedCell {
                    full_text: raw.to_string(),
                    display_text,
                    detected: CellFormat::Plain,
                    error_note: Some("Invalid JSON, showing raw text".to_string()),
                    total_chars,
                    is_truncated,
                }
            }
        },
        CellFormat::Xml => {
            if looks_like_xml(trimmed) {
                let pretty = pretty_print_xml(trimmed);
                let (display_text, total_chars, is_truncated) = cap_display(&pretty);
                FormattedCell {
                    full_text: pretty,
                    display_text,
                    detected: CellFormat::Xml,
                    error_note: None,
                    total_chars,
                    is_truncated,
                }
            } else {
                let (display_text, total_chars, is_truncated) = cap_display(raw);
                FormattedCell {
                    full_text: raw.to_string(),
                    display_text,
                    detected: CellFormat::Plain,
                    error_note: Some("Not XML, showing raw text".to_string()),
                    total_chars,
                    is_truncated,
                }
            }
        }
        CellFormat::Auto => {
            if (trimmed.starts_with('{') || trimmed.starts_with('['))
                && let Ok(val) = serde_json::from_str::<serde_json::Value>(trimmed)
            {
                let pretty = serde_json::to_string_pretty(&val).unwrap_or_else(|_| raw.to_string());
                let (display_text, total_chars, is_truncated) = cap_display(&pretty);
                return FormattedCell {
                    full_text: pretty,
                    display_text,
                    detected: CellFormat::Json,
                    error_note: None,
                    total_chars,
                    is_truncated,
                };
            }
            if looks_like_xml(trimmed) {
                let pretty = pretty_print_xml(trimmed);
                let (display_text, total_chars, is_truncated) = cap_display(&pretty);
                return FormattedCell {
                    full_text: pretty,
                    display_text,
                    detected: CellFormat::Xml,
                    error_note: None,
                    total_chars,
                    is_truncated,
                };
            }
            let (display_text, total_chars, is_truncated) = cap_display(raw);
            FormattedCell {
                full_text: raw.to_string(),
                display_text,
                detected: CellFormat::Plain,
                error_note: None,
                total_chars,
                is_truncated,
            }
        }
    }
}

// ============================================================================
// 6. Cell Preview Syntax Tokenizer (JSON & XML)
// ============================================================================

/// Maximum byte span syntax-highlighted in the value preview pane.
pub const PREVIEW_SYNTAX_BYTE_LIMIT: usize = 50_000;
/// Maximum number of highlight tokens emitted for a single preview value.
pub const PREVIEW_SYNTAX_TOKEN_LIMIT: usize = 5_000;

/// Lexical token kinds colored in the JSON / XML value preview pane.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CellSyntaxTokenKind {
    Property,
    String,
    Number,
    Keyword,
    Punctuation,
    Tag,
    Attribute,
    Comment,
    Declaration,
}

/// A classified UTF-8 byte range `[start, end)` in the preview text.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CellSyntaxToken {
    pub start: usize,
    pub end: usize,
    pub kind: CellSyntaxTokenKind,
}

fn floor_char_boundary(text: &str, max_bytes: usize) -> usize {
    if max_bytes >= text.len() {
        return text.len();
    }
    let mut i = max_bytes;
    while i > 0 && !text.is_char_boundary(i) {
        i -= 1;
    }
    i
}

/// Classify `text` up to `byte_limit` bytes for JSON or XML syntax highlighting.
/// Returned ranges are guaranteed to be valid UTF-8 char boundaries, non-overlapping,
/// and in strictly ascending order (ready for `gpui::StyledText::with_highlights`).
pub fn tokenize_cell_syntax(
    text: &str,
    format: CellFormat,
    byte_limit: usize,
) -> Vec<CellSyntaxToken> {
    if text.is_empty() || byte_limit == 0 {
        return Vec::new();
    }
    let limit = floor_char_boundary(text, byte_limit);
    if limit == 0 {
        return Vec::new();
    }
    match format {
        CellFormat::Json => tokenize_json(text, limit),
        CellFormat::Xml => tokenize_xml(text, limit),
        CellFormat::Auto | CellFormat::Plain => Vec::new(),
    }
}

/// Tokenize with default preview caps, trimming back to the last complete line before the cap
/// so a long value never looks half-highlighted mid-line (matching WinUI's `SyntaxLimit`).
pub fn tokenize_cell_syntax_capped(text: &str, format: CellFormat) -> Vec<CellSyntaxToken> {
    if text.is_empty() || !matches!(format, CellFormat::Json | CellFormat::Xml) {
        return Vec::new();
    }
    let mut tokens = tokenize_cell_syntax(text, format, PREVIEW_SYNTAX_BYTE_LIMIT);
    let hit_byte_cap = text.len() > PREVIEW_SYNTAX_BYTE_LIMIT;
    let hit_token_cap = tokens.len() > PREVIEW_SYNTAX_TOKEN_LIMIT;
    if hit_token_cap {
        tokens.truncate(PREVIEW_SYNTAX_TOKEN_LIMIT);
    }
    if (hit_byte_cap || hit_token_cap) && !tokens.is_empty() {
        let mut cutoff = text.len().min(PREVIEW_SYNTAX_BYTE_LIMIT);
        if let Some(last) = tokens.last() {
            cutoff = cutoff.min(last.end);
        }
        cutoff = floor_char_boundary(text, cutoff);
        if let Some(nl) = text[..cutoff].rfind('\n') {
            let line_end = nl + 1;
            tokens.retain(|t| t.end <= line_end);
        }
    }
    tokens
}

fn next_char_len(text: &str, idx: usize) -> usize {
    text[idx..].chars().next().map_or(1, char::len_utf8)
}

fn scan_quoted(text: &str, start: usize, limit: usize, quote: u8, escapes: bool) -> usize {
    let bytes = text.as_bytes();
    let mut i = start + 1;
    while i < limit {
        let b = bytes[i];
        if escapes && b == b'\\' {
            i += 1;
            if i < limit {
                i += next_char_len(text, i);
            }
            continue;
        }
        if b == quote {
            return i + 1;
        }
        i += next_char_len(text, i);
    }
    limit
}

fn is_followed_by_colon(text: &str, mut i: usize, limit: usize) -> bool {
    let bytes = text.as_bytes();
    while i < limit && bytes[i].is_ascii_whitespace() {
        i += 1;
    }
    i < limit && bytes[i] == b':'
}

fn tokenize_json(text: &str, limit: usize) -> Vec<CellSyntaxToken> {
    let bytes = text.as_bytes();
    let mut tokens = Vec::new();
    let mut i = 0usize;

    while i < limit {
        let b = bytes[i];

        if b == b'"' {
            let end = scan_quoted(text, i, limit, b'"', true);
            let kind = if is_followed_by_colon(text, end, limit) {
                CellSyntaxTokenKind::Property
            } else {
                CellSyntaxTokenKind::String
            };
            tokens.push(CellSyntaxToken {
                start: i,
                end,
                kind,
            });
            i = end;
            continue;
        }

        if b == b'-' || b.is_ascii_digit() {
            let start = i;
            i += 1;
            while i < limit
                && (bytes[i].is_ascii_digit()
                    || matches!(bytes[i], b'.' | b'e' | b'E' | b'+' | b'-'))
            {
                i += 1;
            }
            tokens.push(CellSyntaxToken {
                start,
                end: i,
                kind: CellSyntaxTokenKind::Number,
            });
            continue;
        }

        if b.is_ascii_alphabetic() {
            let start = i;
            while i < limit && bytes[i].is_ascii_alphabetic() {
                i += 1;
            }
            tokens.push(CellSyntaxToken {
                start,
                end: i,
                kind: CellSyntaxTokenKind::Keyword,
            });
            continue;
        }

        if matches!(b, b'{' | b'}' | b'[' | b']' | b',' | b':') {
            tokens.push(CellSyntaxToken {
                start: i,
                end: i + 1,
                kind: CellSyntaxTokenKind::Punctuation,
            });
            i += 1;
            continue;
        }

        i += next_char_len(text, i);
    }

    tokens
}

fn tokenize_xml(text: &str, limit: usize) -> Vec<CellSyntaxToken> {
    let slice = &text[..limit];
    let mut tokens = Vec::new();
    let mut i = 0usize;

    while i < limit {
        let Some(rel) = slice[i..].find('<') else {
            break;
        };
        i += rel;

        if slice[i..].starts_with("<!--") {
            let end = slice[i + 4..]
                .find("-->")
                .map_or(limit, |pos| (i + 4 + pos + 3).min(limit));
            tokens.push(CellSyntaxToken {
                start: i,
                end,
                kind: CellSyntaxTokenKind::Comment,
            });
            i = end;
            continue;
        }

        if slice[i..].starts_with("<![CDATA[") {
            let end = slice[i + 9..]
                .find("]]>")
                .map_or(limit, |pos| (i + 9 + pos + 3).min(limit));
            tokens.push(CellSyntaxToken {
                start: i,
                end,
                kind: CellSyntaxTokenKind::String,
            });
            i = end;
            continue;
        }

        if slice[i..].starts_with("<!") || slice[i..].starts_with("<?") {
            let end = slice[i + 2..]
                .find('>')
                .map_or(limit, |pos| i + 2 + pos + 1);
            tokens.push(CellSyntaxToken {
                start: i,
                end,
                kind: CellSyntaxTokenKind::Declaration,
            });
            i = end;
            continue;
        }

        i = scan_xml_tag(text, i, limit, &mut tokens);
    }

    tokens
}

fn scan_xml_tag(
    text: &str,
    start: usize,
    limit: usize,
    tokens: &mut Vec<CellSyntaxToken>,
) -> usize {
    let bytes = text.as_bytes();
    let mut i = start;

    tokens.push(CellSyntaxToken {
        start: i,
        end: i + 1,
        kind: CellSyntaxTokenKind::Punctuation,
    });
    i += 1;

    if i < limit && bytes[i] == b'/' {
        tokens.push(CellSyntaxToken {
            start: i,
            end: i + 1,
            kind: CellSyntaxTokenKind::Punctuation,
        });
        i += 1;
    }

    let name_start = i;
    while i < limit && !bytes[i].is_ascii_whitespace() && !matches!(bytes[i], b'>' | b'/') {
        i += next_char_len(text, i);
    }
    if i > name_start {
        tokens.push(CellSyntaxToken {
            start: name_start,
            end: i,
            kind: CellSyntaxTokenKind::Tag,
        });
    }

    while i < limit {
        let b = bytes[i];
        if b.is_ascii_whitespace() {
            i += 1;
            continue;
        }
        if matches!(b, b'>' | b'/' | b'=') {
            tokens.push(CellSyntaxToken {
                start: i,
                end: i + 1,
                kind: CellSyntaxTokenKind::Punctuation,
            });
            i += 1;
            if b == b'>' {
                break;
            }
            continue;
        }
        if matches!(b, b'"' | b'\'') {
            let quote_start = i;
            i = scan_quoted(text, i, limit, b, false);
            tokens.push(CellSyntaxToken {
                start: quote_start,
                end: i,
                kind: CellSyntaxTokenKind::String,
            });
            continue;
        }

        let attr_start = i;
        while i < limit
            && !bytes[i].is_ascii_whitespace()
            && !matches!(bytes[i], b'=' | b'>' | b'/' | b'"' | b'\'')
        {
            i += next_char_len(text, i);
        }
        if i > attr_start {
            tokens.push(CellSyntaxToken {
                start: attr_start,
                end: i,
                kind: CellSyntaxTokenKind::Attribute,
            });
        } else {
            i += next_char_len(text, i);
        }
    }

    i
}

#[cfg(test)]
mod tests {
    use std::time::Duration;

    use super::*;
    use crate::{ColumnSchema, ForeignKeySchema, QueryColumn};

    fn sample_tables() -> Vec<TableSchema> {
        vec![
            TableSchema {
                schema: "dbo".into(),
                name: "customers".into(),
                kind: TableKind::Table,
                columns: vec![
                    ColumnSchema {
                        name: "id".into(),
                        data_type: Some("int".into()),
                        nullable: false,
                        is_identity: true,
                        is_computed: false,
                    },
                    ColumnSchema::simple("full_name", Some("nvarchar(120)".into()), false),
                    ColumnSchema::simple("email", Some("nvarchar(200)".into()), true),
                ],
                primary_key: vec!["id".into()],
                foreign_keys: vec![],
            },
            TableSchema {
                schema: "dbo".into(),
                name: "orders".into(),
                kind: TableKind::Table,
                columns: vec![
                    ColumnSchema {
                        name: "id".into(),
                        data_type: Some("int".into()),
                        nullable: false,
                        is_identity: true,
                        is_computed: false,
                    },
                    ColumnSchema::simple("customer_id", Some("int".into()), false),
                    ColumnSchema::simple("status", Some("varchar(32)".into()), false),
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
        ]
    }

    fn make_result(cols: &[(&str, &str)], rows: Vec<Vec<Option<&str>>>, stmt: &str) -> QueryResult {
        QueryResult {
            columns: cols
                .iter()
                .map(|(n, t)| QueryColumn {
                    name: (*n).into(),
                    data_type: Some((*t).into()),
                })
                .collect(),
            rows: rows
                .into_iter()
                .map(|r| r.into_iter().map(|c| c.map(str::to_string)).collect())
                .collect(),
            records_affected: None,
            message: String::new(),
            is_error: false,
            elapsed: Duration::from_millis(1),
            database: Some("demo".into()),
            is_truncated: false,
            row_limit: None,
            messages: Vec::new(),
            statement: Some(stmt.into()),
        }
    }

    #[test]
    fn copy_formats_match_winui_tsv_in_and_values() {
        assert_eq!(escape_tsv_field("=SUM(A1)"), "'=SUM(A1)");
        assert_eq!(escape_tsv_field("a\tb"), "\"a\tb\"");
        assert_eq!(
            format_in_clause(
                SqlDialectKind::TSql,
                &[
                    (Some("42"), Some("int")),
                    (Some("O'Brien"), Some("nvarchar")),
                    (None, Some("nvarchar")),
                ]
            ),
            "IN (42, N'O''Brien', NULL)"
        );
        assert_eq!(
            format_values_clause(
                SqlDialectKind::TSql,
                &[
                    vec![(Some("1"), Some("int")), (Some("Alice"), Some("nvarchar"))],
                    vec![(Some("2"), Some("int")), (None, Some("nvarchar"))],
                ]
            ),
            "VALUES (1, N'Alice'),\n       (2, NULL)"
        );
        // ISO date literal in T-SQL replaces space with T
        assert_eq!(
            format_sql_literal(
                SqlDialectKind::TSql,
                Some("2026-05-01 12:30:00"),
                Some("datetime2")
            ),
            "N'2026-05-01T12:30:00'"
        );
    }

    #[test]
    fn row_edit_resolver_handles_plain_select_identity_and_non_editable_shapes() {
        let tables = sample_tables();
        let stmt = "SELECT * FROM dbo.orders";
        let res = make_result(
            &[("id", "int"), ("customer_id", "int"), ("status", "varchar(32)")],
            vec![vec![Some("10"), Some("3"), Some("pending")]],
            stmt,
        );
        let origins = resolve_column_origins(Some(stmt), &res, &tables);
        assert_eq!(origins.len(), 3);

        let edit = resolve_row_edit(Some(stmt), &res, &tables, &origins);
        assert!(edit.has_editable_targets());
        assert!(!edit.can_edit(0)); // identity PK column is not writable
        assert!(edit.can_edit(1));
        assert!(edit.can_edit(2));

        let mut staged = HashMap::new();
        staged.insert((0, 2), Some("paid".to_string()));
        let deleted = HashSet::new();
        let plan = build_dml_plan(
            SqlDialectKind::TSql,
            &edit,
            &res,
            &tables,
            &staged,
            &deleted,
            Some(2),
        )
        .unwrap();
        assert_eq!(plan.update_count, 1);
        assert_eq!(plan.delete_count, 0);
        assert_eq!(
            plan.statements[0].sql,
            "UPDATE [dbo].[orders] SET [status] = N'paid' WHERE [id] = 10"
        );
        assert!(plan.batch_sql.contains("BEGIN TRANSACTION;"));
        assert!(plan.batch_sql.contains("COMMIT TRANSACTION;"));

        // GROUP BY is rejected
        let grouped = "SELECT status, COUNT(*) AS cnt FROM dbo.orders GROUP BY status";
        let grouped_res = make_result(
            &[("status", "varchar"), ("cnt", "int")],
            vec![vec![Some("paid"), Some("2")]],
            grouped,
        );
        let grouped_origins = resolve_column_origins(Some(grouped), &grouped_res, &tables);
        let grouped_edit = resolve_row_edit(Some(grouped), &grouped_res, &tables, &grouped_origins);
        assert!(!grouped_edit.has_editable_targets());
    }

    #[test]
    fn fk_reference_resolution_and_query_modes_match_winui() {
        let tables = sample_tables();
        let stmt = "SELECT id, customer_id, status FROM dbo.orders";
        let res = make_result(
            &[("id", "int"), ("customer_id", "int"), ("status", "varchar")],
            vec![vec![Some("10"), Some("3"), Some("paid")]],
            stmt,
        );
        let origins = resolve_column_origins(Some(stmt), &res, &tables);
        let refs = resolve_column_references(&origins, &tables);
        let fk = refs.get(&1).expect("customer_id should resolve FK");
        assert_eq!(fk.target_table, "customers");
        assert_eq!(fk.target_column, "id");

        let (filters, warn) = build_reference_filters(fk, &res, 0, &HashMap::new());
        assert!(warn.is_none());
        assert_eq!(filters.len(), 1);

        // Small table (<=200) -> list mode
        let list_plan = build_reference_query(SqlDialectKind::TSql, fk, &filters, Some(40));
        assert!(list_plan.is_list);
        assert_eq!(
            list_plan.sql,
            "SELECT TOP (200) * FROM [dbo].[customers] ORDER BY [id]"
        );

        // Large/unknown table -> filtered lookup mode
        let filtered_plan = build_reference_query(SqlDialectKind::TSql, fk, &filters, Some(5000));
        assert!(!filtered_plan.is_list);
        assert_eq!(
            filtered_plan.sql,
            "SELECT TOP (25) * FROM [dbo].[customers] WHERE [id] = 3"
        );
    }

    #[test]
    fn cell_formatter_detects_and_pretty_prints_json_and_xml() {
        let j = format_cell_content(r#"{"a":1,"b":[2,3]}"#, CellFormat::Auto);
        assert_eq!(j.detected, CellFormat::Json);
        assert!(j.full_text.contains("\n  \"a\": 1"));

        let x = format_cell_content("<root><item id=\"1\">hi</item></root>", CellFormat::Auto);
        assert_eq!(x.detected, CellFormat::Xml);
        assert!(x.full_text.contains("\n  <item id=\"1\">hi</item>"));

        let bad_j = format_cell_content("not json", CellFormat::Json);
        assert_eq!(bad_j.detected, CellFormat::Plain);
        assert!(bad_j.error_note.is_some());
    }

    #[test]
    fn syntax_tokenizer_classifies_json_and_xml_safely() {
        let json = "{\n  \"name\": \"Ada \\\"Lovelace\\\" 🚀\",\n  \"count\": -42.5e2,\n  \"active\": true,\n  \"note\": null\n}";
        let j_tokens = tokenize_cell_syntax(json, CellFormat::Json, usize::MAX);
        assert!(!j_tokens.is_empty());

        let mut prev_end = 0usize;
        for t in &j_tokens {
            assert!(t.start < t.end && t.end <= json.len());
            assert!(json.is_char_boundary(t.start) && json.is_char_boundary(t.end));
            assert!(t.start >= prev_end, "tokens must be ascending & non-overlapping");
            prev_end = t.end;
        }
        let j_pairs: Vec<(&str, CellSyntaxTokenKind)> = j_tokens
            .iter()
            .map(|t| (&json[t.start..t.end], t.kind))
            .collect();
        assert!(j_pairs.contains(&("\"name\"", CellSyntaxTokenKind::Property)));
        assert!(j_pairs.contains(&("\"Ada \\\"Lovelace\\\" 🚀\"", CellSyntaxTokenKind::String)));
        assert!(j_pairs.contains(&("-42.5e2", CellSyntaxTokenKind::Number)));
        assert!(j_pairs.contains(&("true", CellSyntaxTokenKind::Keyword)));
        assert!(j_pairs.contains(&("null", CellSyntaxTokenKind::Keyword)));
        assert!(j_pairs.contains(&("{", CellSyntaxTokenKind::Punctuation)));

        let xml = "<?xml version=\"1.0\"?>\n<!-- greeting -->\n<ns:root id='7' label=\"日本語\">\n  hello\n  <![CDATA[raw <text>]]>\n</ns:root>";
        let x_tokens = tokenize_cell_syntax(xml, CellFormat::Xml, usize::MAX);
        assert!(!x_tokens.is_empty());
        let mut prev_end = 0usize;
        for t in &x_tokens {
            assert!(t.start < t.end && t.end <= xml.len());
            assert!(xml.is_char_boundary(t.start) && xml.is_char_boundary(t.end));
            assert!(t.start >= prev_end);
            prev_end = t.end;
        }
        let x_pairs: Vec<(&str, CellSyntaxTokenKind)> = x_tokens
            .iter()
            .map(|t| (&xml[t.start..t.end], t.kind))
            .collect();
        assert!(x_pairs.contains(&("<?xml version=\"1.0\"?>", CellSyntaxTokenKind::Declaration)));
        assert!(x_pairs.contains(&("<!-- greeting -->", CellSyntaxTokenKind::Comment)));
        assert!(x_pairs.contains(&("ns:root", CellSyntaxTokenKind::Tag)));
        assert!(x_pairs.contains(&("id", CellSyntaxTokenKind::Attribute)));
        assert!(x_pairs.contains(&("'7'", CellSyntaxTokenKind::String)));
        assert!(x_pairs.contains(&("\"日本語\"", CellSyntaxTokenKind::String)));
        assert!(x_pairs.contains(&("<![CDATA[raw <text>]]>", CellSyntaxTokenKind::String)));
        // Plain text nodes remain unhighlighted.
        assert!(!x_pairs.iter().any(|(s, _)| s.contains("hello")));

        // Mid-UTF-8 byte limit must never panic or split a multi-byte character.
        let cut = tokenize_cell_syntax(json, CellFormat::Json, 30);
        for t in &cut {
            assert!(json.is_char_boundary(t.start) && json.is_char_boundary(t.end));
        }
    }
}
