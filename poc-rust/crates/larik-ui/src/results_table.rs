use gpui_kit::component::table::{Column, ColumnFixed, TableDelegate, TableState};
use gpui_kit::component::theme::ActiveTheme;
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::{
    App, Context, IntoElement, ParentElement, SharedString, Styled, TextAlign, Window, div, px,
};
use larik_data::QueryResult;

/// Virtualized `DataTable` delegate rendering a `QueryResult` with a fixed `#` row-number gutter,
/// resizable & movable columns, and muted italic `NULL` cell rendering.
pub struct ResultTableDelegate {
    columns: Vec<Column>,
    column_types: Vec<Option<String>>,
    rows: Vec<Vec<Option<String>>>,
}

impl ResultTableDelegate {
    pub fn empty() -> Self {
        Self {
            columns: Vec::new(),
            column_types: Vec::new(),
            rows: Vec::new(),
        }
    }

    pub fn set_result(&mut self, result: Option<&QueryResult>) {
        self.columns.clear();
        self.column_types.clear();
        self.rows.clear();

        let Some(res) = result else {
            return;
        };
        if res.columns.is_empty() {
            return;
        }

        // Fixed sticky left gutter column (`#`) matching Larik's row-number gutter.
        let mut gutter = Column::new("__row_num__", "#")
            .width(px(56.0))
            .min_width(px(44.0))
            .max_width(px(90.0))
            .fixed(ColumnFixed::Left)
            .resizable(false)
            .movable(false)
            .selectable(false);
        gutter.align = TextAlign::Right;
        self.columns.push(gutter);
        self.column_types.push(None);

        for (idx, col) in res.columns.iter().enumerate() {
            // Estimate comfortable initial column width from header + first few rows.
            let sample_max = res
                .rows
                .iter()
                .take(25)
                .filter_map(|r| r.get(idx).and_then(|v| v.as_deref()))
                .map(|s| s.chars().count())
                .max()
                .unwrap_or(0);
            let char_len = col.name.chars().count().max(sample_max).clamp(8, 36);
            let width = px((char_len as f32 * 8.4 + 28.0).clamp(96.0, 320.0));

            self.columns.push(
                Column::new(format!("col_{idx}"), col.name.clone())
                    .width(width)
                    .min_width(px(64.0))
                    .max_width(px(640.0))
                    .resizable(true)
                    .movable(true),
            );
            self.column_types.push(col.data_type.clone());
        }

        self.rows = res.rows.clone();
    }

    /// Format the active grid as tab-separated values for clipboard export.
    pub fn to_tsv(&self) -> String {
        if self.columns.len() <= 1 {
            return String::new();
        }
        let mut out = String::new();
        let headers: Vec<&str> = self.columns.iter().skip(1).map(|c| c.name.as_ref()).collect();
        out.push_str(&headers.join("\t"));
        out.push('\n');

        for row in &self.rows {
            for (idx, cell) in row.iter().enumerate() {
                if idx > 0 {
                    out.push('\t');
                }
                match cell {
                    Some(v) => out.push_str(&v.replace(['\t', '\r', '\n'], " ")),
                    None => out.push_str("NULL"),
                }
            }
            out.push('\n');
        }
        out
    }
}

impl TableDelegate for ResultTableDelegate {
    fn columns_count(&self, _cx: &App) -> usize {
        self.columns.len()
    }

    fn rows_count(&self, _cx: &App) -> usize {
        self.rows.len()
    }

    fn column(&self, col_ix: usize, _cx: &App) -> Column {
        self.columns.get(col_ix).cloned().unwrap_or_default()
    }

    fn move_column(
        &mut self,
        col_ix: usize,
        to_ix: usize,
        _window: &mut Window,
        _cx: &mut Context<TableState<Self>>,
    ) {
        // Keep column 0 (`#` row gutter) pinned on the far left.
        if col_ix == 0 || to_ix == 0 || col_ix >= self.columns.len() || to_ix >= self.columns.len() {
            return;
        }
        let col = self.columns.remove(col_ix);
        self.columns.insert(to_ix, col);
        let ty = self.column_types.remove(col_ix);
        self.column_types.insert(to_ix, ty);

        let from_data = col_ix - 1;
        let to_data = to_ix - 1;
        for row in &mut self.rows {
            if from_data < row.len() && to_data < row.len() {
                let cell = row.remove(from_data);
                row.insert(to_data, cell);
            }
        }
    }

    fn render_th(
        &mut self,
        col_ix: usize,
        _window: &mut Window,
        cx: &mut Context<TableState<Self>>,
    ) -> impl IntoElement {
        let name = self
            .columns
            .get(col_ix)
            .map(|c| c.name.clone())
            .unwrap_or_default();
        let data_type = self.column_types.get(col_ix).and_then(|t| t.clone());

        if col_ix == 0 {
            return div()
                .w_full()
                .text_right()
                .text_xs()
                .font_family(cx.theme().mono_font_family.clone())
                .text_color(cx.theme().muted_foreground)
                .child("#");
        }

        div()
            .w_full()
            .flex()
            .items_center()
            .gap_1p5()
            .overflow_hidden()
            .child(
                div()
                    .text_xs()
                    .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                    .text_color(cx.theme().foreground)
                    .truncate()
                    .child(name),
            )
            .when_some(data_type, |el, dt| {
                el.child(
                    div()
                        .text_xs()
                        .text_color(cx.theme().muted_foreground.opacity(0.75))
                        .truncate()
                        .child(dt),
                )
            })
    }

    fn render_td(
        &mut self,
        row_ix: usize,
        col_ix: usize,
        _window: &mut Window,
        cx: &mut Context<TableState<Self>>,
    ) -> impl IntoElement {
        let mono = cx.theme().mono_font_family.clone();

        if col_ix == 0 {
            return div()
                .w_full()
                .text_right()
                .text_xs()
                .font_family(mono)
                .text_color(cx.theme().muted_foreground)
                .child(format!("{}", row_ix + 1));
        }

        let cell = self
            .rows
            .get(row_ix)
            .and_then(|r| r.get(col_ix - 1))
            .and_then(|v| v.as_deref());

        match cell {
            Some(text) => div()
                .w_full()
                .text_xs()
                .font_family(mono)
                .text_color(cx.theme().foreground)
                .truncate()
                .child(SharedString::from(text.to_string())),
            None => div()
                .w_full()
                .text_xs()
                .font_family(mono)
                .italic()
                .text_color(cx.theme().muted_foreground.opacity(0.7))
                .child("NULL"),
        }
    }

    fn cell_text(&self, row_ix: usize, col_ix: usize, _cx: &App) -> String {
        if col_ix == 0 {
            return (row_ix + 1).to_string();
        }
        self.rows
            .get(row_ix)
            .and_then(|r| r.get(col_ix - 1))
            .and_then(|v| v.clone())
            .unwrap_or_else(|| "NULL".to_string())
    }
}
