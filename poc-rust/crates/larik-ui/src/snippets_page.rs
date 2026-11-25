use std::collections::BTreeSet;

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{Editor, EditorState, Input, InputEvent, InputState};
use gpui_kit::component::switch::Switch;
use gpui_kit::component::theme::ActiveTheme;
use gpui_kit::component::{Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::{
    AnyElement, App, AppContext as _, Context, Entity, InteractiveElement as _, IntoElement,
    ParentElement as _, SharedString, StatefulInteractiveElement as _, Styled as _, Subscription,
    Window, div, px, rgb, transparent_black,
};
use larik_core::snippets::{
    KNOWN_SNIPPET_DIALECTS, SnippetRecord, has_trigger_conflict, normalize_dialects,
};
use uuid::Uuid;

use crate::studio::StudioView;
use crate::theme::StudioPalette;

pub struct SnippetsPageState {
    pub snippets: Vec<SnippetRecord>,
    pub search_input: Entity<InputState>,
    _search_sub: Subscription,
    pub category_filter: Option<String>,
    pub category_dropdown_open: bool,
    pub selected_id: Option<String>,
    pub creating_new: bool,

    pub editing_id: Option<String>,
    pub editing_is_builtin: bool,
    pub editing_enabled: bool,
    pub editing_dialects: Vec<String>,
    pub trigger_input: Entity<InputState>,
    pub name_input: Entity<InputState>,
    pub category_input: Entity<InputState>,
    pub description_input: Entity<InputState>,
    pub content_editor: Entity<EditorState>,
    pub error_message: Option<String>,
    pub confirming_delete: bool,
}

impl SnippetsPageState {
    pub fn new(
        snippets: Vec<SnippetRecord>,
        window: &mut Window,
        cx: &mut Context<StudioView>,
    ) -> Self {
        let search_input = cx.new(|cx| {
            InputState::new(window, cx).placeholder("Search trigger, name or SQL...")
        });
        let _search_sub = cx.subscribe(&search_input, |_, _, event: &InputEvent, cx| {
            if matches!(event, InputEvent::Change) {
                cx.notify();
            }
        });

        let trigger_input = cx.new(|cx| InputState::new(window, cx).placeholder("e.g. sel, ins, myq"));
        let name_input = cx.new(|cx| InputState::new(window, cx).placeholder("SELECT statement"));
        let category_input =
            cx.new(|cx| InputState::new(window, cx).placeholder("Select, Insert, DDL..."));
        let description_input =
            cx.new(|cx| InputState::new(window, cx).placeholder("What this snippet does"));
        let content_editor = cx.new(|cx| {
            EditorState::new(window, cx)
                .language("sql")
                .placeholder("SELECT * FROM ${cursor}")
        });

        Self {
            snippets,
            search_input,
            _search_sub,
            category_filter: None,
            category_dropdown_open: false,
            selected_id: None,
            creating_new: false,
            editing_id: None,
            editing_is_builtin: false,
            editing_enabled: true,
            editing_dialects: Vec::new(),
            trigger_input,
            name_input,
            category_input,
            description_input,
            content_editor,
            error_message: None,
            confirming_delete: false,
        }
    }

    pub fn select_snippet(&mut self, id: &str, window: &mut Window, cx: &mut App) {
        let Some(rec) = self.snippets.iter().find(|s| s.id == id).cloned() else {
            return;
        };
        self.selected_id = Some(rec.id.clone());
        self.creating_new = false;
        self.category_dropdown_open = false;
        self.editing_id = Some(rec.id);
        self.editing_is_builtin = rec.is_builtin;
        self.editing_enabled = rec.is_enabled;
        self.editing_dialects = rec.dialects.unwrap_or_default();
        self.error_message = None;
        self.confirming_delete = false;

        self.trigger_input
            .update(cx, |i, cx| i.set_value(rec.trigger, window, cx));
        self.name_input
            .update(cx, |i, cx| i.set_value(rec.name, window, cx));
        self.category_input.update(cx, |i, cx| {
            i.set_value(rec.category.unwrap_or_default(), window, cx)
        });
        self.description_input.update(cx, |i, cx| {
            i.set_value(rec.description.unwrap_or_default(), window, cx)
        });
        self.content_editor
            .update(cx, |ed, cx| ed.set_value(rec.content, window, cx));
    }

    pub fn start_new_snippet(&mut self, window: &mut Window, cx: &mut App) {
        self.selected_id = None;
        self.creating_new = true;
        self.category_dropdown_open = false;
        self.editing_id = None;
        self.editing_is_builtin = false;
        self.editing_enabled = true;
        self.editing_dialects.clear();
        self.error_message = None;
        self.confirming_delete = false;

        self.trigger_input
            .update(cx, |i, cx| i.set_value("", window, cx));
        self.name_input
            .update(cx, |i, cx| i.set_value("", window, cx));
        self.category_input
            .update(cx, |i, cx| i.set_value("", window, cx));
        self.description_input
            .update(cx, |i, cx| i.set_value("", window, cx));
        self.content_editor
            .update(cx, |ed, cx| ed.set_value("", window, cx));
        self.trigger_input.update(cx, |i, cx| i.focus(window, cx));
    }

    pub fn cancel_edit(&mut self, window: &mut Window, cx: &mut App) {
        self.error_message = None;
        self.confirming_delete = false;
        if let Some(id) = self.selected_id.clone() {
            self.select_snippet(&id, window, cx);
        } else {
            self.creating_new = false;
        }
    }

    pub fn toggle_dialect(&mut self, dialect: &str) {
        let normalized = dialect.trim().to_ascii_lowercase();
        if let Some(pos) = self
            .editing_dialects
            .iter()
            .position(|d| d.eq_ignore_ascii_case(&normalized))
        {
            self.editing_dialects.remove(pos);
        } else {
            self.editing_dialects.push(normalized);
        }
        self.editing_dialects =
            normalize_dialects(Some(self.editing_dialects.clone())).unwrap_or_default();
    }

    pub fn insert_token(&self, token: &str, window: &mut Window, cx: &mut App) {
        self.content_editor.update(cx, |ed, cx| {
            ed.insert(token, window, cx);
            ed.focus(window, cx);
        });
    }

    pub fn validate_and_collect(&self, cx: &App) -> Result<SnippetRecord, String> {
        let trigger = self.trigger_input.read(cx).value().trim().to_string();
        if trigger.is_empty() {
            return Err("Trigger is required (e.g. sel, myq).".to_string());
        }
        if !trigger
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || c == '_')
        {
            return Err(
                "Trigger can only contain ASCII letters, digits, and underscores (_).".to_string(),
            );
        }

        let name = self.name_input.read(cx).value().trim().to_string();
        if name.is_empty() {
            return Err("Name is required.".to_string());
        }

        let content = self.content_editor.read(cx).value().to_string();
        if content.trim().is_empty() {
            return Err("Snippet SQL content cannot be empty.".to_string());
        }

        let dialects = normalize_dialects(Some(self.editing_dialects.clone()));
        if has_trigger_conflict(
            &self.snippets,
            &trigger,
            dialects.as_deref(),
            self.editing_id.as_deref(),
        ) {
            return Err(format!(
                "Another snippet already uses trigger \"{trigger}\" in an overlapping dialect."
            ));
        }

        let category_raw = self.category_input.read(cx).value().trim().to_string();
        let description_raw = self.description_input.read(cx).value().trim().to_string();

        Ok(SnippetRecord {
            id: self
                .editing_id
                .clone()
                .unwrap_or_else(|| Uuid::new_v4().to_string()),
            trigger,
            name,
            content,
            description: (!description_raw.is_empty()).then_some(description_raw),
            category: (!category_raw.is_empty()).then_some(category_raw),
            dialects,
            is_builtin: self.editing_is_builtin,
            is_enabled: self.editing_enabled,
        })
    }

    pub fn categories(&self) -> Vec<String> {
        let mut set = BTreeSet::new();
        for s in &self.snippets {
            if let Some(cat) = s.category.as_deref().map(str::trim).filter(|c| !c.is_empty()) {
                set.insert(cat.to_string());
            }
        }
        set.into_iter().collect()
    }

    pub fn filtered_snippets(&self, cx: &App) -> Vec<&SnippetRecord> {
        let query = self.search_input.read(cx).value().trim().to_ascii_lowercase();
        let cat = self.category_filter.as_deref();

        self.snippets
            .iter()
            .filter(|s| {
                if let Some(target_cat) = cat {
                    if !s
                        .category
                        .as_deref()
                        .is_some_and(|c| c.eq_ignore_ascii_case(target_cat))
                    {
                        return false;
                    }
                }
                if query.is_empty() {
                    return true;
                }
                s.trigger.to_ascii_lowercase().contains(&query)
                    || s.name.to_ascii_lowercase().contains(&query)
                    || s.content.to_ascii_lowercase().contains(&query)
                    || s.description
                        .as_deref()
                        .is_some_and(|d| d.to_ascii_lowercase().contains(&query))
            })
            .collect()
    }
}

/// Renders the SQL snippets section inside the Settings page, matching the WinUI layout.
pub fn render_snippets_section(
    page: &SnippetsPageState,
    palette: StudioPalette,
    cx: &mut Context<StudioView>,
) -> impl IntoElement {
    let total_count = page.snippets.len();
    let builtin_count = page.snippets.iter().filter(|s| s.is_builtin).count();
    let custom_count = total_count.saturating_sub(builtin_count);
    let disabled_count = page.snippets.iter().filter(|s| !s.is_enabled).count();

    let summary_text = if disabled_count > 0 {
        format!(
            "{total_count} snippets · {builtin_count} built-in · {custom_count} custom · {disabled_count} disabled"
        )
    } else {
        format!("{total_count} snippets · {builtin_count} built-in · {custom_count} custom")
    };

    let categories = page.categories();
    let filtered = page.filtered_snippets(cx);
    let search_non_empty = !page.search_input.read(cx).value().is_empty();
    let editor_active = page.creating_new || page.selected_id.is_some();
    let active_cat_label = page
        .category_filter
        .as_deref()
        .unwrap_or("All categories")
        .to_string();

    let coral_bg = rgb(0xF27B5E);
    let coral_hover = rgb(0xF68E74);
    let coral_fg = rgb(0x1C1412);
    let mono_font = cx.theme().mono_font_family.clone();

    v_flex()
        .size_full()
        .min_h_0()
        .gap_3p5()
        // Header row: "SQL snippets" + count summary | coral "+ New snippet" CTA
        .child(
            h_flex()
                .items_center()
                .justify_between()
                .gap_3()
                .child(
                    v_flex()
                        .gap_0p5()
                        .child(
                            div()
                                .text_size(px(20.0))
                                .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                .text_color(palette.text_primary)
                                .child("SQL snippets"),
                        )
                        .child(
                            div()
                                .text_size(px(12.0))
                                .text_color(palette.text_muted)
                                .child(summary_text),
                        ),
                )
                .child(
                    h_flex()
                        .id("snippets-new-btn")
                        .h(px(32.0))
                        .px_3p5()
                        .rounded(px(6.0))
                        .bg(coral_bg)
                        .hover(|s| s.bg(coral_hover))
                        .items_center()
                        .gap_1p5()
                        .cursor_pointer()
                        .on_click(cx.listener(|this, _, window, cx| {
                            if let Some(page) = this.settings_snippets_mut() {
                                page.start_new_snippet(window, cx);
                            }
                            cx.notify();
                        }))
                        .child(
                            Icon::new(IconName::Plus)
                                .size(px(13.0))
                                .text_color(coral_fg),
                        )
                        .child(
                            div()
                                .text_size(px(12.5))
                                .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                .text_color(coral_fg)
                                .child("New snippet"),
                        ),
                ),
        )
        // 2-column body: List (left) + Editor Card (right)
        .child(
            h_flex()
                .flex_1()
                .min_h_0()
                .gap_4()
                // Left Column: Search, Category dropdown, Scrollable snippet list
                .child(
                    v_flex()
                        .w(px(325.0))
                        .h_full()
                        .flex_shrink_0()
                        .gap_2()
                        // Search input
                        .child(
                            h_flex()
                                .h(px(34.0))
                                .px_2p5()
                                .rounded(px(6.0))
                                .bg(palette.card_bg)
                                .border_1()
                                .border_color(palette.card_border)
                                .items_center()
                                .gap_1p5()
                                .child(
                                    div().flex_1().min_w_0().child(
                                        Input::new(&page.search_input)
                                            .small()
                                            .appearance(false),
                                    ),
                                )
                                .when(search_non_empty, |el| {
                                    el.child(
                                        Button::new("snippets-search-clear")
                                            .ghost()
                                            .xsmall()
                                            .icon(IconName::X)
                                            .on_click(cx.listener(|this, _, window, cx| {
                                                if let Some(p) = this.settings_snippets() {
                                                    p.search_input.update(cx, |i, cx| {
                                                        i.set_value("", window, cx)
                                                    });
                                                }
                                                cx.notify();
                                            })),
                                    )
                                }),
                        )
                        // Category filter dropdown ("All categories ⌄")
                        .child(
                            v_flex()
                                .gap_1()
                                .child(
                                    h_flex()
                                        .id("snippets-cat-dropdown-btn")
                                        .h(px(34.0))
                                        .px_3()
                                        .rounded(px(6.0))
                                        .bg(palette.card_bg)
                                        .border_1()
                                        .border_color(palette.card_border)
                                        .items_center()
                                        .justify_between()
                                        .cursor_pointer()
                                        .hover(|s| s.bg(palette.subtle_hover))
                                        .on_click(cx.listener(|this, _, _, cx| {
                                            if let Some(p) = this.settings_snippets_mut() {
                                                p.category_dropdown_open =
                                                    !p.category_dropdown_open;
                                            }
                                            cx.notify();
                                        }))
                                        .child(
                                            div()
                                                .text_size(px(12.5))
                                                .text_color(palette.text_primary)
                                                .child(active_cat_label),
                                        )
                                        .child(
                                            Icon::new(if page.category_dropdown_open {
                                                IconName::ChevronUp
                                            } else {
                                                IconName::ChevronDown
                                            })
                                            .size(px(12.5))
                                            .text_color(palette.text_muted),
                                        ),
                                )
                                .when(page.category_dropdown_open, |el| {
                                    el.child(
                                        v_flex()
                                            .p_1()
                                            .gap_0p5()
                                            .rounded(px(6.0))
                                            .bg(palette.modal_bg)
                                            .border_1()
                                            .border_color(palette.card_border)
                                            .child(render_category_option(
                                                "All categories",
                                                None,
                                                page.category_filter.is_none(),
                                                palette,
                                                cx,
                                            ))
                                            .children(categories.into_iter().map(|cat| {
                                                let selected = page
                                                    .category_filter
                                                    .as_deref()
                                                    .is_some_and(|c| c.eq_ignore_ascii_case(&cat));
                                                render_category_option(
                                                    &cat,
                                                    Some(cat.clone()),
                                                    selected,
                                                    palette,
                                                    cx,
                                                )
                                            })),
                                    )
                                }),
                        )
                        // Scrollable snippet list
                        .child(
                            div()
                                .id("snippets-list-card")
                                .flex_1()
                                .min_h_0()
                                .overflow_y_scroll()
                                .pr_1()
                                .child(if filtered.is_empty() {
                                    div()
                                        .px_3()
                                        .py_6()
                                        .text_xs()
                                        .text_color(palette.text_muted)
                                        .child("No snippets match this search.")
                                        .into_any_element()
                                } else {
                                    v_flex()
                                        .gap_1()
                                        .children(filtered.into_iter().map(|snip| {
                                            render_snippet_row(
                                                snip,
                                                !page.creating_new
                                                    && page.selected_id.as_deref()
                                                        == Some(snip.id.as_str()),
                                                &mono_font,
                                                palette,
                                                cx,
                                            )
                                        }))
                                        .into_any_element()
                                }),
                        ),
                )
                // Right Column: Snippet Editor Card
                .child(
                    div()
                        .flex_1()
                        .min_w_0()
                        .h_full()
                        .rounded(px(10.0))
                        .bg(palette.card_bg)
                        .border_1()
                        .border_color(palette.card_border)
                        .p_5()
                        .child(if !editor_active {
                            div()
                                .size_full()
                                .flex()
                                .items_center()
                                .justify_center()
                                .text_size(px(12.5))
                                .text_color(palette.text_muted)
                                .child("Select a snippet to edit, or create a new one.")
                                .into_any_element()
                        } else {
                            render_snippet_editor(page, &mono_font, palette, cx).into_any_element()
                        }),
                ),
        )
}

fn render_category_option(
    label: &str,
    value: Option<String>,
    selected: bool,
    palette: StudioPalette,
    cx: &mut Context<StudioView>,
) -> AnyElement {
    let id = SharedString::from(format!(
        "snip-cat-opt-{}",
        value.as_deref().unwrap_or("all").to_ascii_lowercase()
    ));
    let label_owned = label.to_string();
    h_flex()
        .id(id)
        .px_2p5()
        .py_1p5()
        .rounded(px(5.0))
        .items_center()
        .justify_between()
        .cursor_pointer()
        .when(selected, |d| d.bg(palette.subtle_hover))
        .hover(|s| s.bg(palette.subtle_hover))
        .on_click(cx.listener(move |this, _, _, cx| {
            if let Some(page) = this.settings_snippets_mut() {
                page.category_filter = value.clone();
                page.category_dropdown_open = false;
            }
            cx.notify();
        }))
        .child(
            div()
                .text_size(px(12.0))
                .font_weight(if selected {
                    gpui_kit::FontWeight::MEDIUM
                } else {
                    gpui_kit::FontWeight::NORMAL
                })
                .text_color(palette.text_primary)
                .child(label_owned),
        )
        .when(selected, |d| {
            d.child(
                Icon::new(IconName::Check)
                    .size(px(12.5))
                    .text_color(palette.accent),
            )
        })
        .into_any_element()
}

fn render_snippet_row(
    snip: &SnippetRecord,
    selected: bool,
    mono_font: &SharedString,
    palette: StudioPalette,
    cx: &mut Context<StudioView>,
) -> AnyElement {
    let snip_id = snip.id.clone();
    let preview: String = snip
        .content
        .lines()
        .map(str::trim)
        .filter(|l| !l.is_empty())
        .take(1)
        .collect::<Vec<_>>()
        .join(" ");

    let origin_label = if snip.is_builtin {
        "BUILT-IN"
    } else {
        "CUSTOM"
    };
    let cat_label = snip.category.as_deref().unwrap_or("").to_string();

    h_flex()
        .id(SharedString::from(format!("snip-row-{}", snip.id)))
        .px_2p5()
        .py_2()
        .rounded(px(7.0))
        .items_center()
        .justify_between()
        .gap_2()
        .cursor_pointer()
        .when(!snip.is_enabled, |d| d.opacity(0.55))
        .when(selected, |d| {
            d.bg(palette.tab_active_bg)
                .border_1()
                .border_color(palette.card_border)
        })
        .when(!selected, |d| d.hover(|s| s.bg(palette.subtle_hover)))
        .on_click(cx.listener(move |this, _, window, cx| {
            if let Some(page) = this.settings_snippets_mut() {
                page.select_snippet(&snip_id, window, cx);
            }
            cx.notify();
        }))
        .child(
            v_flex()
                .flex_1()
                .min_w_0()
                .gap_0p5()
                .child(
                    h_flex()
                        .items_center()
                        .gap_1p5()
                        .min_w_0()
                        .child(
                            div()
                                .px_1p5()
                                .py(px(1.0))
                                .rounded(px(4.0))
                                .bg(palette.subtle_fill)
                                .font_family(mono_font.clone())
                                .text_size(px(10.5))
                                .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                .text_color(palette.accent)
                                .flex_shrink_0()
                                .child(snip.trigger.clone()),
                        )
                        .child(
                            div()
                                .text_size(px(12.0))
                                .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                .text_color(palette.text_primary)
                                .truncate()
                                .child(snip.name.clone()),
                        ),
                )
                .child(
                    div()
                        .font_family(mono_font.clone())
                        .text_size(px(10.5))
                        .text_color(palette.text_muted)
                        .truncate()
                        .child(preview),
                ),
        )
        .child(
            v_flex()
                .flex_shrink_0()
                .items_end()
                .gap_0p5()
                .child(
                    div()
                        .px_1p5()
                        .py(px(1.0))
                        .rounded(px(3.0))
                        .bg(palette.subtle_fill)
                        .text_size(px(8.5))
                        .font_weight(gpui_kit::FontWeight::BOLD)
                        .text_color(palette.text_muted)
                        .child(origin_label),
                )
                .child(
                    h_flex()
                        .items_center()
                        .gap_1()
                        .when(!snip.is_enabled, |d| {
                            d.child(
                                div()
                                    .text_size(px(9.5))
                                    .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                    .text_color(palette.warning)
                                    .child("OFF"),
                            )
                        })
                        .when(!cat_label.is_empty(), |d| {
                            d.child(
                                div()
                                    .text_size(px(9.5))
                                    .text_color(palette.text_muted)
                                    .child(cat_label),
                            )
                        }),
                ),
        )
        .into_any_element()
}

fn render_snippet_editor(
    page: &SnippetsPageState,
    mono_font: &SharedString,
    palette: StudioPalette,
    cx: &mut Context<StudioView>,
) -> impl IntoElement {
    let heading = if page.creating_new {
        "New snippet"
    } else {
        "Edit snippet"
    };
    let subheading = if page.creating_new {
        "Create a reusable SQL snippet expanded from the completion menu."
    } else if page.editing_is_builtin {
        "Built-in snippet — edits are persisted and can be reset to the catalog default."
    } else {
        "Custom snippet — type the trigger in the SQL editor and press Tab or Enter to expand."
    };

    let all_dialects = page.editing_dialects.is_empty();

    v_flex()
        .size_full()
        .gap_3()
        // Header + Enabled switch
        .child(
            h_flex()
                .items_start()
                .justify_between()
                .gap_3()
                .child(
                    v_flex()
                        .gap_0p5()
                        .child(
                            div()
                                .text_size(px(14.5))
                                .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                .text_color(palette.text_primary)
                                .child(heading),
                        )
                        .child(
                            div()
                                .text_size(px(11.0))
                                .text_color(palette.text_muted)
                                .child(subheading),
                        ),
                )
                .child(
                    h_flex()
                        .items_center()
                        .gap_2()
                        .child(
                            div()
                                .text_xs()
                                .text_color(palette.text_secondary)
                                .child(if page.editing_enabled {
                                    "Enabled"
                                } else {
                                    "Disabled"
                                }),
                        )
                        .child(
                            Switch::new("snippet-enabled-switch")
                                .checked(page.editing_enabled)
                                .on_click(cx.listener(|this, new_val: &bool, _, cx| {
                                    this.toggle_active_snippet_enabled(*new_val, cx);
                                })),
                        ),
                ),
        )
        // Row 1: Trigger + Name
        .child(
            h_flex()
                .gap_3()
                .child(
                    v_flex()
                        .flex_1()
                        .gap_1()
                        .child(field_label("Trigger", true, palette))
                        .child(Input::new(&page.trigger_input).small()),
                )
                .child(
                    v_flex()
                        .flex_1()
                        .gap_1()
                        .child(field_label("Name", true, palette))
                        .child(Input::new(&page.name_input).small()),
                ),
        )
        // Row 2: Category + Description
        .child(
            h_flex()
                .gap_3()
                .child(
                    v_flex()
                        .flex_1()
                        .gap_1()
                        .child(field_label("Category", false, palette))
                        .child(Input::new(&page.category_input).small()),
                )
                .child(
                    v_flex()
                        .flex_1()
                        .gap_1()
                        .child(field_label("Description", false, palette))
                        .child(Input::new(&page.description_input).small()),
                ),
        )
        // Row 3: Dialects
        .child(
            v_flex()
                .gap_1()
                .child(field_label("Dialects", false, palette))
                .child(
                    h_flex()
                        .flex_wrap()
                        .gap_1p5()
                        .child(
                            div()
                                .id("snip-dialect-all")
                                .px_2p5()
                                .py_1()
                                .rounded(px(5.0))
                                .border_1()
                                .border_color(if all_dialects {
                                    palette.accent
                                } else {
                                    palette.hairline
                                })
                                .bg(if all_dialects {
                                    palette.accent_soft
                                } else {
                                    transparent_black()
                                })
                                .text_xs()
                                .text_color(if all_dialects {
                                    palette.text_primary
                                } else {
                                    palette.text_secondary
                                })
                                .cursor_pointer()
                                .hover(|s| s.bg(palette.subtle_hover))
                                .on_click(cx.listener(|this, _, _, cx| {
                                    if let Some(p) = this.settings_snippets_mut() {
                                        p.editing_dialects.clear();
                                    }
                                    cx.notify();
                                }))
                                .child("All dialects"),
                        )
                        .children(KNOWN_SNIPPET_DIALECTS.iter().map(|(id, label)| {
                            let active = page
                                .editing_dialects
                                .iter()
                                .any(|d| d.eq_ignore_ascii_case(id));
                            let id_owned = (*id).to_string();
                            div()
                                .id(SharedString::from(format!("snip-dialect-{id}")))
                                .px_2p5()
                                .py_1()
                                .rounded(px(5.0))
                                .border_1()
                                .border_color(if active {
                                    palette.accent
                                } else {
                                    palette.hairline
                                })
                                .bg(if active {
                                    palette.accent_soft
                                } else {
                                    transparent_black()
                                })
                                .text_xs()
                                .text_color(if active {
                                    palette.text_primary
                                } else {
                                    palette.text_secondary
                                })
                                .cursor_pointer()
                                .hover(|s| s.bg(palette.subtle_hover))
                                .on_click(cx.listener(move |this, _, _, cx| {
                                    if let Some(p) = this.settings_snippets_mut() {
                                        p.toggle_dialect(&id_owned);
                                    }
                                    cx.notify();
                                }))
                                .child(*label)
                        })),
                )
                .child(
                    div()
                        .text_size(px(10.5))
                        .text_color(palette.text_muted)
                        .child("Leave all unchecked (All dialects) to suggest in every SQL dialect."),
                ),
        )
        // Row 4: SQL Content + token chips
        .child(
            v_flex()
                .flex_1()
                .min_h(px(150.0))
                .gap_1p5()
                .child(
                    h_flex()
                        .items_center()
                        .justify_between()
                        .gap_2()
                        .child(
                            h_flex()
                                .items_center()
                                .gap_2()
                                .child(field_label("Content", true, palette))
                                .child(
                                    div()
                                        .text_size(px(10.5))
                                        .text_color(palette.text_muted)
                                        .child("Use ${cursor}, ${1:default}, ${paste}"),
                                ),
                        )
                        .child(
                            h_flex()
                                .gap_1()
                                .child(token_chip("${cursor}", mono_font, palette, cx))
                                .child(token_chip("${1:default}", mono_font, palette, cx))
                                .child(token_chip("${paste}", mono_font, palette, cx)),
                        ),
                )
                .child(
                    div()
                        .flex_1()
                        .min_h(px(130.0))
                        .rounded(px(6.0))
                        .bg(palette.panel_bg)
                        .border_1()
                        .border_color(palette.hairline)
                        .overflow_hidden()
                        .child(
                            Editor::new(&page.content_editor)
                                .appearance(false)
                                .bordered(false)
                                .h_full(),
                        ),
                ),
        )
        // Error banner
        .when_some(page.error_message.as_ref(), |el, msg| {
            el.child(
                h_flex()
                    .px_3()
                    .py_2()
                    .rounded(px(6.0))
                    .bg(palette.danger.opacity(0.12))
                    .border_1()
                    .border_color(palette.danger.opacity(0.35))
                    .items_center()
                    .gap_2()
                    .child(
                        Icon::new(IconName::CircleAlert)
                            .size(px(14.0))
                            .text_color(palette.danger),
                    )
                    .child(
                        div()
                            .text_xs()
                            .text_color(palette.text_primary)
                            .child(msg.clone()),
                    ),
            )
        })
        // Footer buttons
        .child(
            h_flex()
                .pt_1()
                .items_center()
                .justify_between()
                .gap_2()
                .child(
                    h_flex()
                        .gap_2()
                        .when(!page.creating_new && page.editing_is_builtin, |el| {
                            el.child(
                                Button::new("snip-reset-btn")
                                    .outline()
                                    .small()
                                    .icon(IconName::RotateCcw)
                                    .label("Reset to default")
                                    .on_click(cx.listener(|this, _, window, cx| {
                                        this.reset_active_snippet(window, cx);
                                    })),
                            )
                        })
                        .when(!page.creating_new && !page.editing_is_builtin, |el| {
                            if page.confirming_delete {
                                el.child(
                                    Button::new("snip-del-keep")
                                        .ghost()
                                        .small()
                                        .label("Keep")
                                        .on_click(cx.listener(|this, _, _, cx| {
                                            if let Some(p) = this.settings_snippets_mut() {
                                                p.confirming_delete = false;
                                            }
                                            cx.notify();
                                        })),
                                )
                                .child(
                                    Button::new("snip-del-confirm")
                                        .danger()
                                        .small()
                                        .label("Delete snippet")
                                        .on_click(cx.listener(|this, _, window, cx| {
                                            this.delete_active_snippet(window, cx);
                                        })),
                                )
                            } else {
                                el.child(
                                    Button::new("snip-del-arm")
                                        .ghost()
                                        .small()
                                        .icon(IconName::Trash)
                                        .label("Delete")
                                        .on_click(cx.listener(|this, _, _, cx| {
                                            if let Some(p) = this.settings_snippets_mut() {
                                                p.confirming_delete = true;
                                            }
                                            cx.notify();
                                        })),
                                )
                            }
                        }),
                )
                .child(
                    h_flex()
                        .gap_2()
                        .child(
                            Button::new("snip-cancel-btn")
                                .ghost()
                                .small()
                                .label("Cancel")
                                .on_click(cx.listener(|this, _, window, cx| {
                                    if let Some(p) = this.settings_snippets_mut() {
                                        p.cancel_edit(window, cx);
                                    }
                                    cx.notify();
                                })),
                        )
                        .child(
                            Button::new("snip-save-btn")
                                .primary()
                                .small()
                                .icon(IconName::Check)
                                .label("Save snippet")
                                .on_click(cx.listener(|this, _, window, cx| {
                                    this.save_active_snippet(window, cx);
                                })),
                        ),
                ),
        )
}

fn field_label(label: &str, required: bool, palette: StudioPalette) -> AnyElement {
    h_flex()
        .gap_1()
        .child(
            div()
                .text_xs()
                .font_weight(gpui_kit::FontWeight::MEDIUM)
                .text_color(palette.text_secondary)
                .child(label.to_string()),
        )
        .when(required, |el| {
            el.child(div().text_xs().text_color(palette.danger).child("*"))
        })
        .into_any_element()
}

fn token_chip(
    token: &'static str,
    mono_font: &SharedString,
    palette: StudioPalette,
    cx: &mut Context<StudioView>,
) -> AnyElement {
    div()
        .id(SharedString::from(format!("snip-tok-{token}")))
        .px_2()
        .py(px(2.0))
        .rounded(px(4.0))
        .bg(palette.subtle_fill)
        .border_1()
        .border_color(palette.hairline)
        .font_family(mono_font.clone())
        .text_size(px(10.5))
        .text_color(palette.text_secondary)
        .cursor_pointer()
        .hover(|s| {
            s.bg(palette.subtle_hover)
                .text_color(palette.text_primary)
        })
        .on_click(cx.listener(move |this, _, window, cx| {
            if let Some(p) = this.settings_snippets() {
                p.insert_token(token, window, cx);
            }
        }))
        .child(token)
        .into_any_element()
}
