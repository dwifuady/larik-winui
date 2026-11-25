use chrono::{DateTime, Local, NaiveDate, NaiveDateTime, Utc};

/// Upper bound on stored statement size, matching WinUI `RunLogStore.MaxRecordedStatementChars`.
pub const MAX_RECORDED_STATEMENT_CHARS: usize = 32_768;

/// Default run-log retention window in days.
pub const DEFAULT_RUN_LOG_RETENTION_DAYS: i32 = 30;

/// Supported retention choices in days, matching WinUI `RunLogStore.RetentionChoices`.
pub const RUN_LOG_RETENTION_CHOICES: [i32; 3] = [30, 90, 120];

pub mod run_actor {
    pub const USER: &str = "user";
    pub const AGENT: &str = "agent";
}

pub mod run_status {
    pub const OK: &str = "ok";
    pub const FAILED: &str = "failed";
    pub const REFUSED: &str = "refused";
    pub const CANCELLED: &str = "cancelled";
}

/// One persisted execution entry in the `run_log` table.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RunLogRecord {
    pub id: i64,
    /// SQLite UTC timestamp (`YYYY-MM-DD HH:MM:SS`).
    pub recorded_at: String,
    pub actor: String,
    pub space_id: Option<String>,
    pub space_name: Option<String>,
    pub database_name: Option<String>,
    pub provider_id: Option<String>,
    pub tab_id: Option<String>,
    pub statement: String,
    pub status: String,
    pub reason: Option<String>,
    pub duration_ms: i64,
    pub rows_returned: i64,
    pub row_limit: Option<i64>,
    pub is_truncated: bool,
    pub description: Option<String>,
    pub reference: Option<String>,
}

impl RunLogRecord {
    pub fn new_user_run(
        space_id: Option<String>,
        space_name: Option<String>,
        database_name: Option<String>,
        provider_id: Option<String>,
        tab_id: Option<String>,
        statement: String,
        status: impl Into<String>,
        reason: Option<String>,
        duration_ms: i64,
        rows_returned: i64,
        row_limit: Option<i64>,
        is_truncated: bool,
    ) -> Self {
        Self {
            id: 0,
            recorded_at: String::new(),
            actor: run_actor::USER.to_string(),
            space_id,
            space_name,
            database_name,
            provider_id,
            tab_id,
            statement,
            status: status.into(),
            reason,
            duration_ms,
            rows_returned,
            row_limit,
            is_truncated,
            description: None,
            reference: None,
        }
    }

    /// Parses SQLite UTC `YYYY-MM-DD HH:MM:SS` (or RFC3339 fallback) into a UTC `DateTime`.
    pub fn recorded_at_utc(&self) -> DateTime<Utc> {
        if let Ok(naive) = NaiveDateTime::parse_from_str(&self.recorded_at, "%Y-%m-%d %H:%M:%S") {
            return DateTime::from_naive_utc_and_offset(naive, Utc);
        }
        if let Ok(dt) = DateTime::parse_from_rfc3339(&self.recorded_at) {
            return dt.with_timezone(&Utc);
        }
        Utc::now()
    }

    pub fn recorded_at_local(&self) -> DateTime<Local> {
        self.recorded_at_utc().with_timezone(&Local)
    }

    pub fn local_date(&self) -> NaiveDate {
        self.recorded_at_local().date_naive()
    }

    pub fn time_label(&self) -> String {
        self.recorded_at_local().format("%H:%M").to_string()
    }

    /// Single-line preview of the SQL statement for compact history rows.
    pub fn statement_preview(&self) -> String {
        let collapsed: String = self
            .statement
            .split_whitespace()
            .collect::<Vec<_>>()
            .join(" ");
        if collapsed.is_empty() {
            return "(empty query)".to_string();
        }
        collapsed
    }
}

pub fn truncate_statement(statement: &str) -> String {
    if statement.chars().count() <= MAX_RECORDED_STATEMENT_CHARS {
        return statement.to_string();
    }
    statement.chars().take(MAX_RECORDED_STATEMENT_CHARS).collect()
}

pub fn normalize_retention_days(days: i32) -> i32 {
    if RUN_LOG_RETENTION_CHOICES.contains(&days) {
        days
    } else {
        DEFAULT_RUN_LOG_RETENTION_DAYS
    }
}

pub fn escape_like_pattern(query: &str) -> String {
    let mut escaped = String::with_capacity(query.len() + 2);
    escaped.push('%');
    for ch in query.chars() {
        match ch {
            '\\' | '%' | '_' => {
                escaped.push('\\');
                escaped.push(ch);
            }
            _ => escaped.push(ch),
        }
    }
    escaped.push('%');
    escaped
}

pub fn format_duration_ms(ms: i64) -> String {
    if ms < 1_000 {
        format!("{ms} ms")
    } else if ms < 60_000 {
        format!("{:.1} s", ms as f64 / 1000.0)
    } else {
        format!("{} m {} s", ms / 60_000, (ms / 1_000) % 60)
    }
}

pub fn format_rows_label(rows: i64, row_limit: Option<i64>, is_truncated: bool) -> String {
    if is_truncated {
        if let Some(limit) = row_limit {
            return format!(
                "first {} of {} rows",
                format_number(rows),
                format_number(limit)
            );
        }
    }
    match rows {
        0 => "no rows".to_string(),
        1 => "1 row".to_string(),
        n => format!("{} rows", format_number(n)),
    }
}

pub fn format_number(n: i64) -> String {
    let negative = n < 0;
    let digits = n.unsigned_abs().to_string();
    let mut out = String::with_capacity(digits.len() + digits.len() / 3 + 1);
    if negative {
        out.push('-');
    }
    let len = digits.len();
    for (i, ch) in digits.chars().enumerate() {
        if i > 0 && (len - i) % 3 == 0 {
            out.push(',');
        }
        out.push(ch);
    }
    out
}

pub fn format_day_label(day: NaiveDate, today: NaiveDate) -> String {
    if day == today {
        return "Today".to_string();
    }
    if let Some(yesterday) = today.pred_opt() {
        if day == yesterday {
            return "Yesterday".to_string();
        }
    }
    if day.format("%Y").to_string() == today.format("%Y").to_string() {
        day.format("%B %-d").to_string()
    } else {
        day.format("%B %-d, %Y").to_string()
    }
}
