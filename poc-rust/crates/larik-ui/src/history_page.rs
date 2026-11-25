use std::collections::HashMap;

use chrono::{Local, NaiveDate, Utc};
use gpui_kit::assets::IconName;
use gpui_kit::component::input::{Input, InputEvent, InputState};
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::{
    AnyElement, App, AppContext as _, Context, Entity, InteractiveElement as _, IntoElement,
    MouseButton, ParentElement as _, SharedString, StatefulInteractiveElement as _, Styled as _,
    Subscription, Window, div, px, rgb,
};
use larik_core::run_log::{
    RUN_LOG_RETENTION_CHOICES, RunLogRecord, format_day_label, format_duration_ms,
    format_rows_label, run_actor, run_status,
};
use larik_core::{
    ArchivedTabState, MAX_AUTO_ARCHIVE_DAYS, MIN_AUTO_ARCHIVE_DAYS, SpaceState, StudioState,
};

use crate::studio::StudioView;
use crate::theme::{StudioPalette, parse_hex_color};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HistorySegment {
    Runs,
    Archived,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RunActorFilter {
    All,
    You,
    Agent,
    Failed,
}

pub struct HistoryPageState {
    pub segment: HistorySegment,
    pub runs_all_workspaces: bool,
    pub run_filter: RunActorFilter,
    pub retention_days: i32,
    pub runs_total_count: usize,
    pub runs_all_workspaces_count: usize,
    pub runs: Vec<RunLogRecord>,
    pub runs_search_input: Entity<InputState>,
    pub _runs_search_sub: Subscription,
    pub archive_search_input: Entity<InputState>,
    pub _archive_search_sub: Subscription,
    pub auto_archive_days_input: Entity<InputState>,
    pub _auto_archive_days_sub: Subscription,
    pub scope_menu_open: bool,
    pub retention_menu_open: bool,
    pub confirming_clear_archive: bool,
    pub banner_status: Option<SharedString>,
}

impl HistoryPageState {
    pub fn open(
        segment: HistorySegment,
        retention_days: i32,
        auto_archive_days: u32,
        window: &mut Window,
        cx: &mut Context<StudioView>,
    ) -> Self {
        let runs_search_input = cx.new(|cx| {
            InputState::new(window, cx).placeholder("Search executed SQL")
        });
        let _runs_search_sub =
            cx.subscribe_in(&runs_search_input, window, |this, _, event: &InputEvent, _, cx| {
                if matches!(event, InputEvent::Change) {
                    this.refresh_history_runs(cx);
                }
            });

        let archive_search_input = cx.new(|cx| {
            InputState::new(window, cx).placeholder("Search archived tabs")
        });
        let _archive_search_sub = cx.subscribe_in(
            &archive_search_input,
            window,
            |_this, _, event: &InputEvent, _, cx| {
                if matches!(event, InputEvent::Change) {
                    cx.notify();
                }
            },
        );

        let auto_archive_days_input = cx.new(|cx| {
            InputState::new(window, cx).default_value(auto_archive_days.to_string())
        });
        let _auto_archive_days_sub = cx.subscribe_in(
            &auto_archive_days_input,
            window,
            |this, input, event: &InputEvent, window, cx| match event {
                InputEvent::Change => {
                    let raw = input.read(cx).value().trim().to_string();
                    if let Ok(parsed) = raw.parse::<u32>() {
                        let clamped = parsed.clamp(MIN_AUTO_ARCHIVE_DAYS, MAX_AUTO_ARCHIVE_DAYS);
                        this.set_auto_archive_days(clamped, false, window, cx);
                    }
                }
                InputEvent::PressEnter { .. } | InputEvent::Blur => {
                    let raw = input.read(cx).value().trim().to_string();
                    let clamped = raw
                        .parse::<u32>()
                        .unwrap_or_else(|_| this.auto_archive_days())
                        .clamp(MIN_AUTO_ARCHIVE_DAYS, MAX_AUTO_ARCHIVE_DAYS);
                    this.set_auto_archive_days(clamped, true, window, cx);
                }
                _ => {}
            },
        );

        Self {
            segment,
            runs_all_workspaces: false,
            run_filter: RunActorFilter::All,
            retention_days,
            runs_total_count: 0,
            runs_all_workspaces_count: 0,
            runs: Vec::new(),
            runs_search_input,
            _runs_search_sub,
            archive_search_input,
            _archive_search_sub,
            auto_archive_days_input,
            _auto_archive_days_sub,
            scope_menu_open: false,
            retention_menu_open: false,
            confirming_clear_archive: false,
            banner_status: None,
        }
    }

    pub fn runs_search_query(&self, cx: &App) -> String {
        self.runs_search_input.read(cx).value().trim().to_string()
    }

    pub fn archive_search_query(&self, cx: &App) -> String {
        self.archive_search_input
            .read(cx)
            .value()
            .trim()
            .to_string()
    }
}

/// Renders the WinUI-faithful History page (Runs + Archived tabs) inside the right main column,
/// keeping the left sidebar rail visible just like WinUI `MainPage.xaml`.
pub fn render_history_page(
    page: &HistoryPageState,
    state: &StudioState,
    active_space: Option<&SpaceState>,
    palette: StudioPalette,
    cx: &mut Context<StudioView>,
) -> impl IntoElement {
    let coral = rgb(0xF27B5E);
    let count_badge = match page.segment {
        HistorySegment::Runs => {
            if page.runs_total_count == 1 {
                "1 run".to_string()
            } else {
                format!("{} runs", page.runs_total_count)
            }
        }
        HistorySegment::Archived => {
            let count = state.archived_tabs.len();
            if count == 1 {
                "1 archived tab".to_string()
            } else {
                format!("{count} archived tabs")
            }
        }
    };

    v_flex()
        .id("history-page-root")
        .relative()
        .flex_1()
        .min_h_0()
        .min_w_0()
        .w_full()
        .px_6()
        .pt_3()
        .pb_5()
        .gap_3()
        .overflow_hidden()
        .on_mouse_down(
            MouseButton::Left,
            cx.listener(|this, _, _, cx| {
                if let Some(h) = this.history_page.as_mut() {
                    if h.scope_menu_open || h.retention_menu_open {
                        h.scope_menu_open = false;
                        h.retention_menu_open = false;
                        cx.notify();
                    }
                }
            }),
        )
        // Header: ← History [count pill]
        .child(
            h_flex()
                .items_center()
                .gap_2p5()
                .child(
                    div()
                        .id("history-back-btn")
                        .size(px(28.0))
                        .rounded(px(6.0))
                        .flex()
                        .items_center()
                        .justify_center()
                        .cursor_pointer()
                        .hover(|s| s.bg(palette.subtle_hover))
                        .on_click(cx.listener(|this, _, _, cx| {
                            this.close_history_page(cx);
                        }))
                        .child(
                            Icon::new(IconName::ArrowLeft)
                                .size(px(14.0))
                                .text_color(palette.text_secondary),
                        ),
                )
                .child(
                    div()
                        .text_size(px(20.0))
                        .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                        .text_color(palette.text_primary)
                        .child("History"),
                )
                .child(
                    div()
                        .px_2p5()
                        .py(px(2.5))
                        .rounded_full()
                        .bg(palette.subtle_fill)
                        .text_size(px(10.5))
                        .text_color(palette.text_secondary)
                        .child(count_badge),
                )
                .when_some(page.banner_status.clone(), |row, msg| {
                    row.child(
                        div()
                            .ml_2()
                            .text_size(px(11.0))
                            .text_color(palette.text_muted)
                            .child(msg),
                    )
                }),
        )
        // Segment bar: Runs | Archived (with coral active underline)
        .child(
            h_flex()
                .items_center()
                .gap_5()
                .pl_1()
                .child(render_segment_item(
                    "history-seg-runs",
                    "Runs",
                    page.segment == HistorySegment::Runs,
                    coral,
                    palette,
                    cx.listener(|this, _, window, cx| {
                        this.switch_history_segment(HistorySegment::Runs, window, cx);
                    }),
                ))
                .child(render_segment_item(
                    "history-seg-archived",
                    "Archived",
                    page.segment == HistorySegment::Archived,
                    coral,
                    palette,
                    cx.listener(|this, _, window, cx| {
                        this.switch_history_segment(HistorySegment::Archived, window, cx);
                    }),
                )),
        )
        // Active segment body
        .child(match page.segment {
            HistorySegment::Runs => render_runs_segment(page, state, active_space, palette, cx)
                .into_any_element(),
            HistorySegment::Archived => {
                render_archived_segment(page, state, palette, cx).into_any_element()
            }
        })
}

fn render_segment_item(
    id: &'static str,
    label: &'static str,
    selected: bool,
    coral: gpui_kit::Rgba,
    palette: StudioPalette,
    on_click: impl Fn(&gpui_kit::ClickEvent, &mut Window, &mut App) + 'static,
) -> impl IntoElement {
    v_flex()
        .id(id)
        .relative()
        .pb_2()
        .items_center()
        .cursor_pointer()
        .on_click(on_click)
        .child(
            div()
                .text_size(px(13.0))
                .font_weight(if selected {
                    gpui_kit::FontWeight::SEMIBOLD
                } else {
                    gpui_kit::FontWeight::NORMAL
                })
                .text_color(if selected {
                    palette.text_primary
                } else {
                    palette.text_secondary
                })
                .hover(|s| s.text_color(palette.text_primary))
                .child(label),
        )
        .when(selected, |el| {
            el.child(
                div()
                    .absolute()
                    .bottom_0()
                    .w(px(18.0))
                    .h(px(2.5))
                    .rounded_full()
                    .bg(coral),
            )
        })
}

fn render_runs_segment(
    page: &HistoryPageState,
    state: &StudioState,
    active_space: Option<&SpaceState>,
    palette: StudioPalette,
    cx: &mut Context<StudioView>,
) -> impl IntoElement {
    let scope_label = if page.runs_all_workspaces {
        "All workspaces".to_string()
    } else {
        active_space
            .map(|s| s.name.clone())
            .unwrap_or_else(|| "This workspace".to_string())
    };
    let retention_label = format!("Keeping {} days", page.retention_days);

    // Filter runs by selected actor/status filter.
    let space_names: HashMap<&str, &str> = state
        .spaces
        .iter()
        .map(|s| (s.id.as_str(), s.name.as_str()))
        .collect();

    let filtered_runs: Vec<&RunLogRecord> = page
        .runs
        .iter()
        .filter(|r| match page.run_filter {
            RunActorFilter::All => true,
            RunActorFilter::You => r.actor != run_actor::AGENT,
            RunActorFilter::Agent => r.actor == run_actor::AGENT,
            RunActorFilter::Failed => r.status != run_status::OK,
        })
        .collect();

    // Group runs by local calendar date, preserving newest-first order.
    let today = Local::now().date_naive();
    let mut day_groups: Vec<(NaiveDate, Vec<&RunLogRecord>)> = Vec::new();
    for run in filtered_runs {
        let day = run.local_date();
        if let Some(last) = day_groups.last_mut().filter(|(d, _)| *d == day) {
            last.1.push(run);
        } else {
            day_groups.push((day, vec![run]));
        }
    }

    let show_all_workspaces_cta =
        !page.runs_all_workspaces && page.runs_all_workspaces_count > 0;
    let mono_font = cx.theme().mono_font_family.clone();

    v_flex()
        .relative()
        .flex_1()
        .min_h_0()
        .w_full()
        .gap_2p5()
        // Toolbar row: [Scope ▾]  [All | You | Agent | Failed]   [Keeping 30 days ▾]  [Search executed SQL]
        .child(
            h_flex()
                .items_center()
                .gap_2()
                // Scope dropdown trigger
                .child(
                    div()
                        .relative()
                        .child(
                            h_flex()
                                .id("runs-scope-btn")
                                .h(px(28.0))
                                .px_2p5()
                                .rounded(px(6.0))
                                .bg(palette.card_bg)
                                .border_1()
                                .border_color(palette.card_border)
                                .items_center()
                                .gap_1p5()
                                .cursor_pointer()
                                .hover(|s| s.bg(palette.subtle_hover))
                                .on_mouse_down(MouseButton::Left, |_, _, cx| {
                                    cx.stop_propagation();
                                })
                                .on_click(cx.listener(|this, _, _, cx| {
                                    if let Some(h) = this.history_page.as_mut() {
                                        h.scope_menu_open = !h.scope_menu_open;
                                        h.retention_menu_open = false;
                                        cx.notify();
                                    }
                                }))
                                .child(
                                    div()
                                        .text_size(px(12.0))
                                        .text_color(palette.text_primary)
                                        .child(scope_label),
                                )
                                .child(
                                    Icon::new(IconName::ChevronDown)
                                        .size(px(10.0))
                                        .text_color(palette.text_muted),
                                ),
                        )
                        .when(page.scope_menu_open, |anchor| {
                            anchor.child(
                                v_flex()
                                    .id("runs-scope-menu")
                                    .absolute()
                                    .top(px(32.0))
                                    .left_0()
                                    .w(px(164.0))
                                    .p_1()
                                    .rounded(px(6.0))
                                    .bg(palette.modal_bg)
                                    .border_1()
                                    .border_color(palette.card_border)
                                    .shadow_lg()
                                    .on_mouse_down(MouseButton::Left, |_, _, cx| {
                                        cx.stop_propagation();
                                    })
                                    .child(render_menu_choice(
                                        "scope-this",
                                        "This workspace",
                                        !page.runs_all_workspaces,
                                        palette,
                                        cx.listener(|this, _, _, cx| {
                                            this.set_history_runs_all_workspaces(false, cx);
                                        }),
                                    ))
                                    .child(render_menu_choice(
                                        "scope-all",
                                        "All workspaces",
                                        page.runs_all_workspaces,
                                        palette,
                                        cx.listener(|this, _, _, cx| {
                                            this.set_history_runs_all_workspaces(true, cx);
                                        }),
                                    )),
                            )
                        }),
                )
                // Actor/Status filter pills
                .child(
                    h_flex()
                        .items_center()
                        .gap_0p5()
                        .p(px(2.0))
                        .rounded(px(6.0))
                        .bg(palette.subtle_fill)
                        .children([
                            (RunActorFilter::All, "All", "run-filter-all"),
                            (RunActorFilter::You, "You", "run-filter-you"),
                            (RunActorFilter::Agent, "Agent", "run-filter-agent"),
                            (RunActorFilter::Failed, "Failed", "run-filter-failed"),
                        ]
                        .into_iter()
                        .map(|(filter, label, id)| {
                            let active = page.run_filter == filter;
                            div()
                                .id(id)
                                .px_2p5()
                                .py(px(3.5))
                                .rounded(px(5.0))
                                .cursor_pointer()
                                .when(active, |d| d.bg(palette.card_bg))
                                .when(!active, |d| d.hover(|s| s.bg(palette.subtle_hover)))
                                .text_size(px(11.5))
                                .font_weight(if active {
                                    gpui_kit::FontWeight::MEDIUM
                                } else {
                                    gpui_kit::FontWeight::NORMAL
                                })
                                .text_color(if active {
                                    palette.text_primary
                                } else {
                                    palette.text_secondary
                                })
                                .on_click(cx.listener(move |this, _, _, cx| {
                                    if let Some(h) = this.history_page.as_mut() {
                                        h.run_filter = filter;
                                        h.scope_menu_open = false;
                                        h.retention_menu_open = false;
                                        cx.notify();
                                    }
                                }))
                                .child(label)
                        })),
                )
                .child(div().flex_1())
                // Retention window dropdown
                .child(
                    div()
                        .relative()
                        .child(
                            h_flex()
                                .id("runs-retention-btn")
                                .h(px(28.0))
                                .px_2()
                                .rounded(px(6.0))
                                .items_center()
                                .gap_1()
                                .cursor_pointer()
                                .hover(|s| s.bg(palette.subtle_hover))
                                .on_mouse_down(MouseButton::Left, |_, _, cx| {
                                    cx.stop_propagation();
                                })
                                .on_click(cx.listener(|this, _, _, cx| {
                                    if let Some(h) = this.history_page.as_mut() {
                                        h.retention_menu_open = !h.retention_menu_open;
                                        h.scope_menu_open = false;
                                        cx.notify();
                                    }
                                }))
                                .child(
                                    div()
                                        .text_size(px(11.0))
                                        .text_color(palette.text_muted)
                                        .child(retention_label),
                                )
                                .child(
                                    Icon::new(IconName::ChevronDown)
                                        .size(px(10.0))
                                        .text_color(palette.text_muted),
                                ),
                        )
                        .when(page.retention_menu_open, |anchor| {
                            anchor.child(
                                v_flex()
                                    .id("runs-retention-menu")
                                    .absolute()
                                    .top(px(32.0))
                                    .right_0()
                                    .w(px(124.0))
                                    .p_1()
                                    .rounded(px(6.0))
                                    .bg(palette.modal_bg)
                                    .border_1()
                                    .border_color(palette.card_border)
                                    .shadow_lg()
                                    .on_mouse_down(MouseButton::Left, |_, _, cx| {
                                        cx.stop_propagation();
                                    })
                                    .children(RUN_LOG_RETENTION_CHOICES.into_iter().map(|days| {
                                        render_menu_choice(
                                            SharedString::from(format!("ret-{days}")),
                                            SharedString::from(format!("{days} days")),
                                            page.retention_days == days,
                                            palette,
                                            cx.listener(move |this, _, _, cx| {
                                                this.set_history_run_retention(days, cx);
                                            }),
                                        )
                                    })),
                            )
                        }),
                )
                // Search executed SQL input
                .child(
                    div()
                        .w(px(210.0))
                        .child(Input::new(&page.runs_search_input).xsmall()),
                ),
        )
        // Runs body or Empty state
        .child(if day_groups.is_empty() {
            v_flex()
                .flex_1()
                .min_h_0()
                .w_full()
                .items_center()
                .justify_center()
                .gap_1p5()
                .child(
                    Icon::new(IconName::RotateCcw)
                        .size(px(26.0))
                        .text_color(palette.text_muted),
                )
                .child(
                    div()
                        .text_size(px(13.0))
                        .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                        .text_color(palette.text_secondary)
                        .child("No runs here yet"),
                )
                .child(
                    div()
                        .max_w(px(360.0))
                        .text_center()
                        .text_size(px(11.0))
                        .text_color(palette.text_muted)
                        .child(
                            "Queries you run — and queries the agent runs — appear here with their timing, row count and workspace.",
                        ),
                )
                .when(show_all_workspaces_cta, |empty| {
                    empty.child(
                        div()
                            .id("runs-show-all-workspaces-btn")
                            .mt_2()
                            .px_3()
                            .py_1p5()
                            .rounded(px(6.0))
                            .bg(palette.subtle_fill)
                            .border_1()
                            .border_color(palette.card_border)
                            .cursor_pointer()
                            .hover(|s| s.bg(palette.subtle_hover))
                            .text_size(px(11.5))
                            .text_color(palette.text_primary)
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.set_history_runs_all_workspaces(true, cx);
                            }))
                            .child("Show all workspaces"),
                    )
                })
                .into_any_element()
        } else {
            div()
                .id("history-runs-scroll")
                .flex_1()
                .min_h_0()
                .w_full()
                .overflow_y_scroll()
                .pr_1()
                .child(v_flex().gap_3().children(day_groups.into_iter().map(
                    |(day, runs)| {
                        let day_label = format_day_label(day, today);
                        v_flex()
                            .gap_1()
                            .child(
                                div()
                                    .pl_0p5()
                                    .pb_0p5()
                                    .text_size(px(10.5))
                                    .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                    .text_color(palette.text_muted)
                                    .child(day_label),
                            )
                            .children(runs.into_iter().map(|run| {
                                render_run_row(
                                    run,
                                    state,
                                    &space_names,
                                    page.runs_all_workspaces,
                                    &mono_font,
                                    palette,
                                    cx,
                                )
                            }))
                    },
                )))
                .into_any_element()
        })
}

fn render_run_row(
    run: &RunLogRecord,
    state: &StudioState,
    space_names: &HashMap<&str, &str>,
    show_workspace: bool,
    mono_font: &SharedString,
    palette: StudioPalette,
    cx: &mut Context<StudioView>,
) -> AnyElement {
    let resolved_space_name = run
        .space_id
        .as_deref()
        .and_then(|id| space_names.get(id).copied())
        .or(run.space_name.as_deref())
        .unwrap_or("Deleted workspace");

    let tab_exists = run.tab_id.as_deref().is_some_and(|tid| {
        state
            .spaces
            .iter()
            .any(|s| s.tabs.iter().any(|t| t.id == tid))
    });

    let mut meta_parts: Vec<String> = Vec::new();
    if show_workspace {
        meta_parts.push(resolved_space_name.to_string());
    }
    if let Some(db) = run.database_name.as_deref().filter(|d| !d.trim().is_empty()) {
        meta_parts.push(db.to_string());
    }
    meta_parts.push(format_duration_ms(run.duration_ms));
    meta_parts.push(format_rows_label(
        run.rows_returned,
        run.row_limit,
        run.is_truncated,
    ));
    if let Some(reason) = run.reason.as_deref().filter(|r| !r.trim().is_empty()) {
        meta_parts.push(reason.to_string());
    }
    let meta_line = meta_parts.join(" · ");

    let is_agent = run.actor == run_actor::AGENT;
    let (status_label, status_color) = match run.status.as_str() {
        run_status::OK => ("ok", palette.success),
        run_status::FAILED => ("failed", palette.danger),
        run_status::REFUSED => ("refused", palette.warning),
        run_status::CANCELLED => ("cancelled", palette.text_muted),
        other => (other, palette.text_muted),
    };
    let action_label = if tab_exists {
        "Go to tab"
    } else {
        "Open in new tab"
    };
    let run_clone = run.clone();
    let run_id = run.id;

    h_flex()
        .id(SharedString::from(format!("run-row-{run_id}")))
        .px_3()
        .py_2()
        .rounded(px(6.0))
        .bg(palette.card_bg)
        .border_1()
        .border_color(palette.card_border)
        .items_center()
        .gap_2p5()
        // Time column (44px)
        .child(
            div()
                .w(px(44.0))
                .flex_shrink_0()
                .text_size(px(11.0))
                .text_color(palette.text_muted)
                .child(run.time_label()),
        )
        // Statement + meta line (+ optional agent notes)
        .child(
            v_flex()
                .flex_1()
                .min_w_0()
                .gap(px(2.0))
                .child(
                    div()
                        .font_family(mono_font.clone())
                        .text_size(px(12.0))
                        .text_color(palette.text_primary)
                        .truncate()
                        .child(run.statement_preview()),
                )
                .child(
                    div()
                        .text_size(px(10.5))
                        .text_color(palette.text_muted)
                        .truncate()
                        .child(meta_line),
                )
                .when(
                    run.description.as_deref().is_some_and(|s| !s.trim().is_empty())
                        || run.reference.as_deref().is_some_and(|s| !s.trim().is_empty()),
                    |col| {
                        col.child(
                            h_flex()
                                .items_center()
                                .gap_1p5()
                                .when_some(
                                    run.reference
                                        .as_deref()
                                        .filter(|s| !s.trim().is_empty())
                                        .map(str::to_string),
                                    |row, reference| {
                                        row.child(
                                            div()
                                                .px_1p5()
                                                .py(px(1.0))
                                                .rounded_full()
                                                .bg(palette.subtle_fill)
                                                .text_size(px(9.5))
                                                .text_color(palette.text_secondary)
                                                .child(reference),
                                        )
                                    },
                                )
                                .when_some(
                                    run.description
                                        .as_deref()
                                        .filter(|s| !s.trim().is_empty())
                                        .map(str::to_string),
                                    |row, desc| {
                                        row.child(
                                            div()
                                                .flex_1()
                                                .min_w_0()
                                                .text_size(px(10.5))
                                                .text_color(palette.text_secondary)
                                                .truncate()
                                                .child(desc),
                                        )
                                    },
                                ),
                        )
                    },
                ),
        )
        // Actor pill + Status + Action button
        .child(
            h_flex()
                .flex_shrink_0()
                .items_center()
                .gap_2()
                .child(
                    div()
                        .px_1p5()
                        .py(px(1.5))
                        .rounded_full()
                        .bg(if is_agent {
                            palette.accent_soft
                        } else {
                            palette.subtle_fill
                        })
                        .text_size(px(9.5))
                        .text_color(if is_agent {
                            palette.text_primary
                        } else {
                            palette.text_muted
                        })
                        .child(if is_agent { "agent" } else { "you" }),
                )
                .child(
                    div()
                        .text_size(px(10.5))
                        .text_color(status_color)
                        .child(status_label.to_string()),
                )
                .child(
                    div()
                        .id(SharedString::from(format!("run-open-{run_id}")))
                        .px_2p5()
                        .py(px(4.0))
                        .rounded(px(5.0))
                        .cursor_pointer()
                        .hover(|s| s.bg(palette.subtle_hover))
                        .text_size(px(11.0))
                        .text_color(palette.text_primary)
                        .on_click(cx.listener(move |this, _, window, cx| {
                            this.open_history_run(run_clone.clone(), window, cx);
                        }))
                        .child(action_label),
                ),
        )
        .into_any_element()
}

fn render_archived_segment(
    page: &HistoryPageState,
    state: &StudioState,
    palette: StudioPalette,
    cx: &mut Context<StudioView>,
) -> impl IntoElement {
    let coral = rgb(0xF27B5E);
    let query = page.archive_search_query(cx).to_ascii_lowercase();
    let now = Utc::now();
    let days = state.effective_auto_archive_days();
    let auto_enabled = state.auto_archive_enabled;
    let subtitle = if auto_enabled {
        format!(
            "Tabs untouched for {days} {} are archived automatically. Pinned tabs are never archived.",
            if days == 1 { "day" } else { "days" }
        )
    } else {
        "Auto-archive is off. Pinned tabs are never archived.".to_string()
    };

    let space_lookup: HashMap<&str, (&str, &str)> = state
        .spaces
        .iter()
        .map(|s| (s.id.as_str(), (s.name.as_str(), s.color.as_str())))
        .collect();

    let filtered: Vec<&ArchivedTabState> = state
        .archived_tabs
        .iter()
        .filter(|tab| {
            if query.is_empty() {
                return true;
            }
            let space_name = space_lookup
                .get(tab.space_id.as_str())
                .map(|(n, _)| *n)
                .or(tab.space_name.as_deref())
                .unwrap_or("");
            tab.title.to_ascii_lowercase().contains(&query)
                || space_name.to_ascii_lowercase().contains(&query)
                || tab
                    .database
                    .as_deref()
                    .unwrap_or("")
                    .to_ascii_lowercase()
                    .contains(&query)
                || tab.content.to_ascii_lowercase().contains(&query)
        })
        .collect();

    let has_any_archived = !state.archived_tabs.is_empty();
    let mono_font = cx.theme().mono_font_family.clone();

    v_flex()
        .flex_1()
        .min_h_0()
        .w_full()
        .gap_2p5()
        // Right-aligned toolbar: [Search archived tabs] [Clear all]
        .child(
            h_flex()
                .w_full()
                .justify_end()
                .items_center()
                .gap_2()
                .child(
                    div()
                        .w(px(220.0))
                        .child(Input::new(&page.archive_search_input).xsmall()),
                )
                .when(has_any_archived && !page.confirming_clear_archive, |bar| {
                    bar.child(
                        div()
                            .id("archive-clear-all-btn")
                            .h(px(28.0))
                            .px_2p5()
                            .rounded(px(6.0))
                            .flex()
                            .items_center()
                            .cursor_pointer()
                            .hover(|s| s.bg(palette.subtle_hover))
                            .text_size(px(11.5))
                            .text_color(palette.text_secondary)
                            .on_click(cx.listener(|this, _, _, cx| {
                                if let Some(h) = this.history_page.as_mut() {
                                    h.confirming_clear_archive = true;
                                    cx.notify();
                                }
                            }))
                            .child("Clear all"),
                    )
                })
                .when(has_any_archived && page.confirming_clear_archive, |bar| {
                    bar.child(
                        h_flex()
                            .items_center()
                            .gap_1()
                            .child(
                                div()
                                    .id("archive-clear-confirm-btn")
                                    .h(px(28.0))
                                    .px_2p5()
                                    .rounded(px(6.0))
                                    .bg(palette.danger.opacity(0.18))
                                    .border_1()
                                    .border_color(palette.danger.opacity(0.4))
                                    .flex()
                                    .items_center()
                                    .cursor_pointer()
                                    .hover(|s| s.bg(palette.danger.opacity(0.28)))
                                    .text_size(px(11.5))
                                    .font_weight(gpui_kit::FontWeight::MEDIUM)
                                    .text_color(palette.danger)
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.clear_all_archived_tabs(cx);
                                    }))
                                    .child("Delete all"),
                            )
                            .child(
                                div()
                                    .id("archive-clear-cancel-btn")
                                    .h(px(28.0))
                                    .px_2()
                                    .rounded(px(6.0))
                                    .flex()
                                    .items_center()
                                    .cursor_pointer()
                                    .hover(|s| s.bg(palette.subtle_hover))
                                    .text_size(px(11.5))
                                    .text_color(palette.text_muted)
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        if let Some(h) = this.history_page.as_mut() {
                                            h.confirming_clear_archive = false;
                                            cx.notify();
                                        }
                                    }))
                                    .child("Cancel"),
                            ),
                    )
                }),
        )
        // Auto-archive card matching WinUI screenshot
        .child(
            v_flex()
                .w_full()
                .px_3p5()
                .py_3()
                .rounded(px(8.0))
                .bg(palette.card_bg)
                .border_1()
                .border_color(palette.card_border)
                .gap_2()
                .child(
                    h_flex()
                        .items_center()
                        .gap_3()
                        // Toggle switch + "Auto-archive" label
                        .child(
                            h_flex()
                                .id("auto-archive-toggle")
                                .items_center()
                                .gap_2p5()
                                .cursor_pointer()
                                .on_click(cx.listener(|this, _, _, cx| {
                                    this.toggle_auto_archive_enabled(cx);
                                }))
                                .child(
                                    div()
                                        .w(px(38.0))
                                        .h(px(20.0))
                                        .px(px(3.0))
                                        .rounded_full()
                                        .bg(if auto_enabled {
                                            coral.into()
                                        } else {
                                            palette.subtle_fill
                                        })
                                        .border_1()
                                        .border_color(if auto_enabled {
                                            coral.into()
                                        } else {
                                            palette.card_border
                                        })
                                        .flex()
                                        .items_center()
                                        .when(auto_enabled, |t| t.justify_end())
                                        .when(!auto_enabled, |t| t.justify_start())
                                        .child(
                                            div()
                                                .size(px(13.0))
                                                .rounded_full()
                                                .bg(if auto_enabled {
                                                    rgb(0x1C1412).into()
                                                } else {
                                                    palette.text_muted
                                                }),
                                        ),
                                )
                                .child(
                                    div()
                                        .text_size(px(12.5))
                                        .text_color(palette.text_primary)
                                        .child("Auto-archive"),
                                ),
                        )
                        // Compact numeric days box with up/down spin buttons (matching WinUI NumberBox)
                        .child(
                            h_flex()
                                .w(px(110.0))
                                .h(px(30.0))
                                .rounded(px(6.0))
                                .bg(palette.subtle_fill)
                                .border_1()
                                .border_color(palette.card_border)
                                .items_center()
                                .overflow_hidden()
                                .child(
                                    div().flex_1().min_w_0().px_1().child(
                                        Input::new(&page.auto_archive_days_input)
                                            .xsmall()
                                            .appearance(false),
                                    ),
                                )
                                .child(
                                    v_flex()
                                        .w(px(22.0))
                                        .h_full()
                                        .border_l_1()
                                        .border_color(palette.hairline_subtle)
                                        .child(
                                            div()
                                                .id("auto-archive-days-inc")
                                                .flex_1()
                                                .w_full()
                                                .flex()
                                                .items_center()
                                                .justify_center()
                                                .cursor_pointer()
                                                .hover(|s| s.bg(palette.subtle_hover))
                                                .on_click(cx.listener(|this, _, window, cx| {
                                                    let next = this
                                                        .auto_archive_days()
                                                        .saturating_add(1)
                                                        .min(MAX_AUTO_ARCHIVE_DAYS);
                                                    this.set_auto_archive_days(
                                                        next, true, window, cx,
                                                    );
                                                }))
                                                .child(
                                                    Icon::new(IconName::ChevronUp)
                                                        .size(px(9.5))
                                                        .text_color(palette.text_muted),
                                                ),
                                        )
                                        .child(
                                            div()
                                                .id("auto-archive-days-dec")
                                                .flex_1()
                                                .w_full()
                                                .flex()
                                                .items_center()
                                                .justify_center()
                                                .cursor_pointer()
                                                .hover(|s| s.bg(palette.subtle_hover))
                                                .on_click(cx.listener(|this, _, window, cx| {
                                                    let next = this
                                                        .auto_archive_days()
                                                        .saturating_sub(1)
                                                        .max(MIN_AUTO_ARCHIVE_DAYS);
                                                    this.set_auto_archive_days(
                                                        next, true, window, cx,
                                                    );
                                                }))
                                                .child(
                                                    Icon::new(IconName::ChevronDown)
                                                        .size(px(9.5))
                                                        .text_color(palette.text_muted),
                                                ),
                                        ),
                                ),
                        )
                        .child(
                            div()
                                .text_size(px(11.5))
                                .text_color(palette.text_secondary)
                                .child("days without use"),
                        )
                        .child(div().flex_1())
                        // Right-aligned "Run now" button
                        .child(
                            div()
                                .id("auto-archive-run-now")
                                .px_2p5()
                                .py_1p5()
                                .rounded(px(5.0))
                                .cursor_pointer()
                                .hover(|s| s.bg(palette.subtle_hover))
                                .text_size(px(11.5))
                                .text_color(palette.text_primary)
                                .on_click(cx.listener(|this, _, window, cx| {
                                    this.run_auto_archive_now(window, cx);
                                }))
                                .child("Run now"),
                        ),
                )
                .child(
                    div()
                        .text_size(px(10.5))
                        .text_color(palette.text_muted)
                        .child(subtitle),
                ),
        )
        // Archived list OR centered empty state
        .child(if filtered.is_empty() {
            v_flex()
                .flex_1()
                .min_h_0()
                .w_full()
                .items_center()
                .justify_center()
                .gap_1p5()
                .child(
                    Icon::new(IconName::RotateCcw)
                        .size(px(26.0))
                        .text_color(palette.text_muted),
                )
                .child(
                    div()
                        .text_size(px(13.0))
                        .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                        .text_color(palette.text_secondary)
                        .child(if query.is_empty() {
                            "No archived tabs"
                        } else {
                            "No matching archived tabs"
                        }),
                )
                .child(
                    div()
                        .max_w(px(360.0))
                        .text_center()
                        .text_size(px(11.0))
                        .text_color(palette.text_muted)
                        .child(
                            "Tabs you archive — or that auto-archive after their inactive days — are kept here. Pinned tabs are never auto-archived.",
                        ),
                )
                .into_any_element()
        } else {
            div()
                .id("history-archived-scroll")
                .flex_1()
                .min_h_0()
                .w_full()
                .overflow_y_scroll()
                .pr_1()
                .child(v_flex().gap_1p5().children(filtered.into_iter().map(|arch| {
                    let (space_name, color_hex) = space_lookup
                        .get(arch.space_id.as_str())
                        .copied()
                        .unwrap_or((
                            arch.space_name.as_deref().unwrap_or("Workspace"),
                            arch.color.as_deref().unwrap_or("#6366F1"),
                        ));
                    let accent = parse_hex_color(color_hex);
                    let context_line = match arch.database.as_deref().filter(|d| !d.trim().is_empty()) {
                        Some(db) => format!("{space_name} · {db}"),
                        None => space_name.to_string(),
                    };
                    let restore_id = arch.id.clone();
                    let delete_id = arch.id.clone();
                    let age = arch.age_label(now);
                    let preview = arch.preview();

                    h_flex()
                        .id(SharedString::from(format!("arch-card-{}", arch.id)))
                        .px_3p5()
                        .py_2p5()
                        .rounded(px(8.0))
                        .bg(palette.card_bg)
                        .border_1()
                        .border_color(palette.card_border)
                        .items_center()
                        .gap_3()
                        // 2px accent pill bar on left
                        .child(
                            div()
                                .w(px(2.5))
                                .h(px(38.0))
                                .rounded_full()
                                .flex_shrink_0()
                                .bg(accent),
                        )
                        // Title + context + SQL preview
                        .child(
                            v_flex()
                                .flex_1()
                                .min_w_0()
                                .gap(px(2.0))
                                .child(
                                    h_flex()
                                        .items_center()
                                        .gap_1p5()
                                        .when(arch.is_pinned, |row| {
                                            row.child(
                                                Icon::new(IconName::Star)
                                                    .size(px(10.5))
                                                    .text_color(palette.text_muted),
                                            )
                                        })
                                        .child(
                                            div()
                                                .text_size(px(12.0))
                                                .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                                .text_color(palette.text_primary)
                                                .truncate()
                                                .child(arch.title.clone()),
                                        ),
                                )
                                .child(
                                    div()
                                        .text_size(px(10.5))
                                        .text_color(palette.text_muted)
                                        .truncate()
                                        .child(context_line),
                                )
                                .child(
                                    div()
                                        .pt(px(2.0))
                                        .font_family(mono_font.clone())
                                        .text_size(px(11.0))
                                        .text_color(palette.text_secondary)
                                        .truncate()
                                        .child(preview),
                                ),
                        )
                        // Relative age
                        .child(
                            div()
                                .flex_shrink_0()
                                .text_size(px(10.5))
                                .text_color(palette.text_muted)
                                .child(age),
                        )
                        // Restore + Delete actions
                        .child(
                            h_flex()
                                .flex_shrink_0()
                                .items_center()
                                .gap_1()
                                .child(
                                    div()
                                        .id(SharedString::from(format!("arch-restore-{}", arch.id)))
                                        .px_2p5()
                                        .py_1p5()
                                        .rounded(px(5.0))
                                        .cursor_pointer()
                                        .hover(|s| s.bg(palette.subtle_hover))
                                        .text_size(px(11.5))
                                        .text_color(palette.text_primary)
                                        .on_click(cx.listener(move |this, _, window, cx| {
                                            this.restore_archived_tab(
                                                restore_id.clone(),
                                                window,
                                                cx,
                                            );
                                        }))
                                        .child("Restore"),
                                )
                                .child(
                                    div()
                                        .id(SharedString::from(format!("arch-delete-{}", arch.id)))
                                        .size(px(28.0))
                                        .rounded(px(5.0))
                                        .flex()
                                        .items_center()
                                        .justify_center()
                                        .cursor_pointer()
                                        .hover(|s| s.bg(palette.danger.opacity(0.14)))
                                        .on_click(cx.listener(move |this, _, _, cx| {
                                            this.delete_archived_tab(delete_id.clone(), cx);
                                        }))
                                        .child(
                                            Icon::new(IconName::Trash)
                                                .size(px(12.0))
                                                .text_color(palette.text_muted),
                                        ),
                                ),
                        )
                })))
                .into_any_element()
        })
}

fn render_menu_choice(
    id: impl Into<gpui_kit::ElementId>,
    label: impl Into<SharedString>,
    selected: bool,
    palette: StudioPalette,
    on_click: impl Fn(&gpui_kit::ClickEvent, &mut Window, &mut App) + 'static,
) -> impl IntoElement {
    h_flex()
        .id(id)
        .px_2p5()
        .py_1p5()
        .rounded(px(5.0))
        .items_center()
        .justify_between()
        .gap_2()
        .cursor_pointer()
        .hover(|s| s.bg(palette.subtle_hover))
        .on_click(on_click)
        .child(
            div()
                .text_size(px(11.5))
                .text_color(palette.text_primary)
                .child(label.into()),
        )
        .when(selected, |row| {
            row.child(
                Icon::new(IconName::Check)
                    .size(px(11.5))
                    .text_color(palette.accent),
            )
        })
}
