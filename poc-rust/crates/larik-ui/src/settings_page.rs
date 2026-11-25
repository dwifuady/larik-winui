use std::path::Path;
use std::sync::Arc;

use gpui_kit::assets::IconName;
use gpui_kit::component::button::Button;
use gpui_kit::component::theme::ActiveTheme;
use gpui_kit::component::{Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::{
    AnyElement, Context, Image, InteractiveElement as _, IntoElement, ParentElement as _,
    SharedString, StatefulInteractiveElement as _, Styled as _, Window, div, px, rgb,
    transparent_black,
};
use larik_core::ThemePreference;
use larik_core::snippets::SnippetRecord;

use crate::snippets_page::{SnippetsPageState, render_snippets_section};
use crate::studio::StudioView;
use crate::theme::StudioPalette;
use crate::window_title_bar::render_app_title_bar;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SettingsSection {
    Appearance,
    Snippets,
    About,
}

pub struct SettingsPageState {
    pub active_section: SettingsSection,
    pub nav_collapsed: bool,
    pub snippets: SnippetsPageState,
}

impl SettingsPageState {
    pub fn new(
        snippets: Vec<SnippetRecord>,
        window: &mut Window,
        cx: &mut Context<StudioView>,
    ) -> Self {
        Self {
            // Match WinUI SettingsPage default section (Snippets)
            active_section: SettingsSection::Snippets,
            nav_collapsed: false,
            snippets: SnippetsPageState::new(snippets, window, cx),
        }
    }
}

pub fn render_settings_page(
    page: &SettingsPageState,
    theme_pref: ThemePreference,
    db_path: &Path,
    app_icon: &Arc<Image>,
    palette: StudioPalette,
    window: &Window,
    cx: &mut Context<StudioView>,
) -> impl IntoElement {
    let nav_width = if page.nav_collapsed {
        px(52.0)
    } else {
        px(210.0)
    };
    let collapsed = page.nav_collapsed;
    let active_section = page.active_section;

    v_flex()
        .size_full()
        .overflow_hidden()
        // Top Windows-safe TitleBar: clickable Back arrow + Larik icon + "Settings"
        .child(render_app_title_bar(
            "settings",
            IconName::ArrowLeft,
            app_icon,
            "Settings",
            palette,
            window,
            cx,
            cx.listener(|this, _, _, cx| {
                this.close_settings_page(cx);
            }),
        ))
        // Body: WinUI NavigationView left rail + active section content
        .child(
            h_flex()
                .flex_1()
                .min_h_0()
                .w_full()
                .overflow_hidden()
                // Left NavigationView pane (Appearance, Snippets, About — no Drivers / AI agents)
                .child(
                    v_flex()
                        .w(nav_width)
                        .h_full()
                        .flex_shrink_0()
                        .px_2()
                        .pt_1()
                        .pb_3()
                        .gap_1()
                        // Collapse/expand hamburger button
                        .child(
                            div()
                                .id("settings-nav-toggle")
                                .size(px(34.0))
                                .rounded(px(6.0))
                                .flex()
                                .items_center()
                                .justify_center()
                                .cursor_pointer()
                                .hover(|s| s.bg(palette.subtle_hover))
                                .on_click(cx.listener(|this, _, _, cx| {
                                    if let Some(p) = this.settings_page.as_mut() {
                                        p.nav_collapsed = !p.nav_collapsed;
                                    }
                                    cx.notify();
                                }))
                                .child(
                                    Icon::new(IconName::Menu)
                                        .size(px(14.0))
                                        .text_color(palette.text_secondary),
                                ),
                        )
                        .child(div().h(px(4.0)))
                        .child(render_nav_item(
                            "appearance",
                            "Appearance",
                            IconName::Palette,
                            SettingsSection::Appearance,
                            active_section == SettingsSection::Appearance,
                            collapsed,
                            palette,
                            cx,
                        ))
                        .child(render_nav_item(
                            "snippets",
                            "Snippets",
                            IconName::Braces,
                            SettingsSection::Snippets,
                            active_section == SettingsSection::Snippets,
                            collapsed,
                            palette,
                            cx,
                        ))
                        .child(render_nav_item(
                            "about",
                            "About",
                            IconName::Info,
                            SettingsSection::About,
                            active_section == SettingsSection::About,
                            collapsed,
                            palette,
                            cx,
                        )),
                )
                // Right section host
                .child(
                    div()
                        .flex_1()
                        .min_w_0()
                        .h_full()
                        .px_6()
                        .pt_2()
                        .pb_5()
                        .overflow_hidden()
                        .child(match active_section {
                            SettingsSection::Appearance => {
                                render_appearance_section(theme_pref, palette, cx)
                                    .into_any_element()
                            }
                            SettingsSection::Snippets => {
                                render_snippets_section(&page.snippets, palette, cx)
                                    .into_any_element()
                            }
                            SettingsSection::About => {
                                render_about_section(db_path, palette, cx).into_any_element()
                            }
                        }),
                ),
        )
}

#[allow(clippy::too_many_arguments)]
fn render_nav_item(
    id: &'static str,
    label: &'static str,
    icon: IconName,
    section: SettingsSection,
    selected: bool,
    collapsed: bool,
    palette: StudioPalette,
    cx: &mut Context<StudioView>,
) -> AnyElement {
    let coral_accent = rgb(0xF27B5E);

    h_flex()
        .id(SharedString::from(format!("settings-nav-{id}")))
        .relative()
        .h(px(36.0))
        .px_2p5()
        .rounded(px(6.0))
        .items_center()
        .gap_3()
        .cursor_pointer()
        .when(selected, |d| d.bg(palette.subtle_hover))
        .when(!selected, |d| d.hover(|s| s.bg(palette.subtle_hover)))
        .on_click(cx.listener(move |this, _, _, cx| {
            if let Some(p) = this.settings_page.as_mut() {
                p.active_section = section;
            }
            cx.notify();
        }))
        // WinUI left selection pill indicator
        .when(selected, |d| {
            d.child(
                div()
                    .absolute()
                    .left(px(0.0))
                    .top(px(10.0))
                    .bottom(px(10.0))
                    .w(px(3.0))
                    .rounded_full()
                    .bg(coral_accent),
            )
        })
        .child(
            Icon::new(icon)
                .size(px(14.5))
                .text_color(if selected {
                    palette.text_primary
                } else {
                    palette.text_secondary
                }),
        )
        .when(!collapsed, |d| {
            d.child(
                div()
                    .text_size(px(13.0))
                    .font_weight(if selected {
                        gpui_kit::FontWeight::MEDIUM
                    } else {
                        gpui_kit::FontWeight::NORMAL
                    })
                    .text_color(if selected {
                        palette.text_primary
                    } else {
                        palette.text_secondary
                    })
                    .truncate()
                    .child(label),
            )
        })
        .into_any_element()
}

fn render_appearance_section(
    theme_pref: ThemePreference,
    palette: StudioPalette,
    cx: &mut Context<StudioView>,
) -> impl IntoElement {
    div()
        .id("settings-appearance-scroll")
        .size_full()
        .overflow_y_scroll()
        .child(
            v_flex()
                .max_w(px(680.0))
                .gap_3p5()
                .child(
                    v_flex()
                        .gap_1()
                        .child(
                            div()
                                .text_size(px(20.0))
                                .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                .text_color(palette.text_primary)
                                .child("Appearance"),
                        )
                        .child(
                            div()
                                .text_size(px(12.0))
                                .text_color(palette.text_muted)
                                .child(
                                    "Choose the theme for the studio. The choice is remembered on this device.",
                                ),
                        ),
                )
                .child(
                    v_flex()
                        .p_5()
                        .gap_3p5()
                        .rounded(px(8.0))
                        .bg(palette.card_bg)
                        .border_1()
                        .border_color(palette.card_border)
                        .child(
                            v_flex()
                                .gap_0p5()
                                .child(
                                    div()
                                        .text_size(px(14.0))
                                        .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                        .text_color(palette.text_primary)
                                        .child("Theme"),
                                )
                                .child(
                                    div()
                                        .text_size(px(11.5))
                                        .text_color(palette.text_muted)
                                        .child("System follows the Windows light and dark setting."),
                                ),
                        )
                        .child(
                            v_flex()
                                .gap_2()
                                .child(render_theme_radio(
                                    "system",
                                    "Use Windows setting",
                                    ThemePreference::System,
                                    theme_pref == ThemePreference::System,
                                    palette,
                                    cx,
                                ))
                                .child(render_theme_radio(
                                    "light",
                                    "Light",
                                    ThemePreference::Light,
                                    theme_pref == ThemePreference::Light,
                                    palette,
                                    cx,
                                ))
                                .child(render_theme_radio(
                                    "dark",
                                    "Dark",
                                    ThemePreference::Dark,
                                    theme_pref == ThemePreference::Dark,
                                    palette,
                                    cx,
                                )),
                        ),
                )
                .child(
                    div()
                        .text_size(px(11.5))
                        .text_color(palette.text_muted)
                        .child(
                            "Larik redraws open tabs and results immediately; nothing needs a restart.",
                        ),
                ),
        )
}

fn render_theme_radio(
    id: &'static str,
    label: &'static str,
    value: ThemePreference,
    selected: bool,
    palette: StudioPalette,
    cx: &mut Context<StudioView>,
) -> AnyElement {
    let coral_accent = rgb(0xF27B5E);

    h_flex()
        .id(SharedString::from(format!("settings-theme-{id}")))
        .px_2()
        .py_1p5()
        .rounded(px(6.0))
        .items_center()
        .gap_2p5()
        .cursor_pointer()
        .hover(|s| s.bg(palette.subtle_hover))
        .on_click(cx.listener(move |this, _, window, cx| {
            this.set_theme(value, window, cx);
        }))
        .child(
            div()
                .size(px(18.0))
                .rounded_full()
                .border_2()
                .border_color(if selected {
                    coral_accent.into()
                } else {
                    palette.text_muted
                })
                .bg(if selected {
                    coral_accent.into()
                } else {
                    transparent_black()
                })
                .flex()
                .items_center()
                .justify_center()
                .when(selected, |d| {
                    d.child(div().size(px(7.0)).rounded_full().bg(rgb(0x1C1412)))
                }),
        )
        .child(
            div()
                .text_size(px(13.0))
                .text_color(palette.text_primary)
                .child(label),
        )
        .into_any_element()
}

fn render_about_section(
    db_path: &Path,
    palette: StudioPalette,
    cx: &mut Context<StudioView>,
) -> impl IntoElement {
    let mono_font = cx.theme().mono_font_family.clone();
    let db_path_str = db_path.display().to_string();
    let data_dir = db_path
        .parent()
        .map(|p| p.to_path_buf())
        .unwrap_or_else(|| db_path.to_path_buf());
    let version = env!("CARGO_PKG_VERSION");

    div()
        .id("settings-about-scroll")
        .size_full()
        .overflow_y_scroll()
        .child(
            v_flex()
                .max_w(px(680.0))
                .gap_3()
                .child(
                    div()
                        .text_size(px(20.0))
                        .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                        .text_color(palette.text_primary)
                        .child("Larik SQL Studio"),
                )
                .child(
                    div()
                        .text_size(px(12.5))
                        .text_color(palette.text_muted)
                        .child(
                            "A native Windows SQL studio for exploring databases and writing queries.",
                        ),
                )
                .child(
                    div()
                        .text_size(px(12.0))
                        .text_color(palette.text_muted)
                        .child(format!("Version {version} · MIT licensed")),
                )
                .child(
                    div()
                        .pt_3()
                        .text_size(px(10.0))
                        .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                        .text_color(palette.text_muted)
                        .child("LOCAL DATA"),
                )
                .child(
                    h_flex()
                        .p_3p5()
                        .gap_4()
                        .rounded(px(8.0))
                        .bg(palette.card_bg)
                        .border_1()
                        .border_color(palette.card_border)
                        .items_center()
                        .justify_between()
                        .child(
                            v_flex()
                                .flex_1()
                                .min_w_0()
                                .gap_1()
                                .child(
                                    div()
                                        .text_size(px(11.5))
                                        .text_color(palette.text_secondary)
                                        .child(
                                            "Workspaces, tabs and snippets are stored in this SQLite database:",
                                        ),
                                )
                                .child(
                                    div()
                                        .font_family(mono_font)
                                        .text_size(px(11.5))
                                        .text_color(palette.text_primary)
                                        .child(db_path_str),
                                ),
                        )
                        .child(
                            Button::new("about-open-data-folder")
                                .outline()
                                .small()
                                .label("Open folder")
                                .on_click(move |_, _, _| {
                                    #[cfg(target_os = "windows")]
                                    {
                                        let _ = std::process::Command::new("explorer")
                                            .arg(&data_dir)
                                            .spawn();
                                    }
                                    #[cfg(not(target_os = "windows"))]
                                    {
                                        let _ = &data_dir;
                                    }
                                }),
                        ),
                )
                .child(
                    div()
                        .pt_1()
                        .text_size(px(11.5))
                        .text_color(palette.text_muted)
                        .child(
                            "Snippets are scoped per database dialect. Built-in snippets can be edited or disabled but not deleted; custom snippets can be deleted.",
                        ),
                ),
        )
}
