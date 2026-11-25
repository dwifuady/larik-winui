use std::collections::HashMap;
use std::sync::Arc;

use gpui_kit::assets::IconName;
use gpui_kit::component::input::{Input, InputEvent, InputState};
use gpui_kit::component::switch::Switch;
use gpui_kit::component::theme::ActiveTheme;
use gpui_kit::component::{Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::{
    AnyElement, App, AppContext as _, Context, Entity, InteractiveElement as _, IntoElement,
    MouseButton, ParentElement as _, SharedString, StatefulInteractiveElement as _, Styled as _,
    Subscription, Window, div, hsla, px, rgb,
};
use larik_core::SPACE_COLORS;
use larik_data::{ConnectionProfile, DatabaseProvider, ProviderCatalog, ProviderSettingKind};

use crate::theme::{StudioPalette, parse_hex_color};

/// Curated set of Lucide icons available for workspace identity badges.
pub const WORKSPACE_ICONS: &[(&str, &str, IconName)] = &[
    ("database", "Database", IconName::Database),
    ("server", "Server", IconName::Server),
    ("cloud", "Cloud", IconName::Cloud),
    ("hard-drive", "Storage", IconName::HardDrive),
    ("layers", "Layers", IconName::Layers),
    ("terminal", "Terminal", IconName::Terminal),
    ("code", "Code", IconName::Code),
    ("box", "Container", IconName::Box),
    ("folder", "Folder", IconName::Folder),
    ("globe", "Globe", IconName::Globe),
    ("shield", "Shield", IconName::Shield),
    ("lock", "Lock", IconName::Lock),
    ("key", "Key", IconName::Key),
    ("cpu", "Compute", IconName::Cpu),
    ("zap", "Fast", IconName::Zap),
    ("flame", "Production", IconName::Flame),
    ("rocket", "Launch", IconName::Rocket),
    ("star", "Star", IconName::Star),
    ("briefcase", "Work", IconName::Briefcase),
    ("building", "Enterprise", IconName::Building),
];

/// Resolves a stored workspace icon key into its bundled Lucide `IconName`.
pub fn workspace_icon_name(icon: Option<&str>) -> Option<IconName> {
    let key = icon?.trim();
    if key.is_empty() {
        return None;
    }
    WORKSPACE_ICONS
        .iter()
        .find(|(id, _, _)| id.eq_ignore_ascii_case(key))
        .map(|(_, _, name)| *name)
}

/// First-letter monogram fallback for workspaces without a custom icon.
pub fn workspace_monogram(name: &str) -> String {
    name.trim()
        .chars()
        .next()
        .map(|c| c.to_uppercase().to_string())
        .unwrap_or_else(|| "W".to_string())
}

/// Actions dispatched by the Workspace settings modal to its host view.
#[derive(Debug, Clone)]
pub enum ConnectionModalAction {
    Close,
    ClearName,
    ToggleIconPicker,
    PickIcon(Option<String>),
    ToggleColorPicker,
    PickColor(String),
    ToggleProviderDropdown,
    SwitchProvider(String),
    ToggleBool(String, bool),
    ToggleAdvanced,
    Test,
    Save,
    SetConfirmDelete(bool),
    ConfirmDelete,
}

/// State backing the provider-generated Workspace settings modal.
pub struct ConnectionModal {
    pub space_id: String,
    pub name_input: Entity<InputState>,
    pub color: String,
    pub icon: Option<String>,
    pub provider_id: String,
    pub text_inputs: HashMap<String, Entity<InputState>>,
    pub bool_values: HashMap<String, bool>,
    pub show_advanced: bool,
    pub show_icon_picker: bool,
    pub show_color_picker: bool,
    pub show_provider_dropdown: bool,
    pub testing: bool,
    pub test_status: Option<Result<String, String>>,
    pub can_delete: bool,
    pub confirming_delete: bool,
    _input_subs: Vec<Subscription>,
}

impl ConnectionModal {
    #[allow(clippy::too_many_arguments)]
    pub fn open<V: 'static>(
        space_id: String,
        space_name: &str,
        space_color: &str,
        space_icon: Option<&str>,
        profile: &ConnectionProfile,
        can_delete: bool,
        catalog: &ProviderCatalog,
        window: &mut Window,
        cx: &mut Context<V>,
    ) -> Self {
        let name_input = cx.new(|cx| {
            InputState::new(window, cx)
                .placeholder("Workspace name")
                .default_value(space_name.to_string())
        });
        let name_sub = cx.subscribe(&name_input, |_, _, event: &InputEvent, cx| {
            if matches!(event, InputEvent::Change) {
                cx.notify();
            }
        });

        let mut modal = Self {
            space_id,
            name_input,
            color: space_color.to_string(),
            icon: space_icon.map(str::to_string),
            provider_id: profile.provider_id.clone(),
            text_inputs: HashMap::new(),
            bool_values: HashMap::new(),
            show_advanced: false,
            show_icon_picker: false,
            show_color_picker: false,
            show_provider_dropdown: false,
            testing: false,
            test_status: None,
            can_delete,
            confirming_delete: false,
            _input_subs: vec![name_sub],
        };
        modal.rebuild_fields(profile, catalog, window, cx);
        modal
    }

    pub fn switch_provider<V: 'static>(
        &mut self,
        provider_id: &str,
        catalog: &ProviderCatalog,
        window: &mut Window,
        cx: &mut Context<V>,
    ) {
        self.show_provider_dropdown = false;
        if self.provider_id.eq_ignore_ascii_case(provider_id) {
            return;
        }
        self.provider_id = provider_id.to_string();
        self.test_status = None;
        let profile = catalog
            .find(provider_id)
            .map(|p| p.default_profile())
            .unwrap_or_else(|| ConnectionProfile::new(provider_id));
        self.rebuild_fields(&profile, catalog, window, cx);
    }

    fn rebuild_fields<V: 'static>(
        &mut self,
        profile: &ConnectionProfile,
        catalog: &ProviderCatalog,
        window: &mut Window,
        cx: &mut Context<V>,
    ) {
        self.text_inputs.clear();
        self.bool_values.clear();
        // Keep index 0 (name_input subscription) and replace field subscriptions.
        self._input_subs.truncate(1);

        let Some(provider) = catalog.find(&self.provider_id) else {
            return;
        };

        for setting in provider.settings() {
            match setting.kind {
                ProviderSettingKind::Boolean => {
                    let default_bool = setting
                        .default_value
                        .is_some_and(|v| v.eq_ignore_ascii_case("true"));
                    let val = profile.get_bool(setting.key, default_bool);
                    self.bool_values.insert(setting.key.to_string(), val);
                }
                _ => {
                    let initial = profile
                        .get(setting.key)
                        .or(setting.default_value)
                        .unwrap_or_default()
                        .to_string();
                    let placeholder = setting.placeholder.unwrap_or_default().to_string();
                    let is_password = setting.kind == ProviderSettingKind::Password;
                    let input = cx.new(|cx| {
                        let mut state = InputState::new(window, cx)
                            .placeholder(placeholder)
                            .default_value(initial);
                        if is_password {
                            state = state.masked(true);
                        }
                        state
                    });
                    // Re-render live transport warnings when server or connection fields change.
                    let sub = cx.subscribe(&input, |_, _, event: &InputEvent, cx| {
                        if matches!(event, InputEvent::Change) {
                            cx.notify();
                        }
                    });
                    self._input_subs.push(sub);
                    self.text_inputs.insert(setting.key.to_string(), input);
                }
            }
        }
    }

    pub fn collect_profile(&self, catalog: &ProviderCatalog, cx: &App) -> ConnectionProfile {
        let mut profile = ConnectionProfile::new(self.provider_id.clone());
        let Some(provider) = catalog.find(&self.provider_id) else {
            return profile;
        };

        for setting in provider.settings() {
            match setting.kind {
                ProviderSettingKind::Boolean => {
                    let val = self.bool_values.get(setting.key).copied().unwrap_or(false);
                    profile.set(setting.key, if val { "true" } else { "false" });
                }
                _ => {
                    if let Some(entity) = self.text_inputs.get(setting.key) {
                        let text = entity.read(cx).value().to_string();
                        if !text.is_empty() {
                            profile.set(setting.key, text);
                        }
                    }
                }
            }
        }
        profile
    }

    pub fn workspace_name(&self, cx: &App) -> String {
        let name = self.name_input.read(cx).value().trim().to_string();
        if name.is_empty() {
            "Workspace".to_string()
        } else {
            name
        }
    }
}

/// Renders the centered WinUI-styled "Workspace settings" modal over the studio window.
pub fn render_connection_modal<V: 'static>(
    modal: &ConnectionModal,
    catalog: &ProviderCatalog,
    palette: StudioPalette,
    cx: &mut Context<V>,
    on_action: impl Fn(&mut V, ConnectionModalAction, &mut Window, &mut Context<V>) + 'static + Clone,
) -> impl IntoElement {
    let providers: Vec<Arc<dyn DatabaseProvider>> = catalog.all().to_vec();
    let active_provider = catalog.find(&modal.provider_id);
    let active_provider_name = active_provider
        .as_ref()
        .map(|p| p.display_name())
        .unwrap_or("Select database");

    let basic_settings: Vec<_> = active_provider
        .as_ref()
        .map(|p| p.settings().iter().filter(|s| !s.is_advanced).collect())
        .unwrap_or_default();
    let advanced_settings: Vec<_> = active_provider
        .as_ref()
        .map(|p| p.settings().iter().filter(|s| s.is_advanced).collect())
        .unwrap_or_default();

    let live_profile = modal.collect_profile(catalog, cx);
    let transport_warning = active_provider
        .as_ref()
        .and_then(|p| p.transport_warning(&live_profile));

    let name_value = modal.name_input.read(cx).value().to_string();
    let name_non_empty = !name_value.is_empty();
    let monogram = workspace_monogram(&name_value);
    let selected_icon = workspace_icon_name(modal.icon.as_deref());
    let swatch_color = parse_hex_color(&modal.color);

    let coral_bg = rgb(0xF27B5E);
    let coral_hover = rgb(0xF68E74);
    let coral_fg = rgb(0x1C1412);

    let act = |action: ConnectionModalAction| {
        let cb = on_action.clone();
        cx.listener(move |this, _: &gpui_kit::ClickEvent, window, cx| {
            cb(this, action.clone(), window, cx)
        })
    };
    let backdrop_close = {
        let cb = on_action.clone();
        cx.listener(move |this, _, window, cx| cb(this, ConnectionModalAction::Close, window, cx))
    };

    div()
        .id("connection-modal-backdrop")
        .absolute()
        .inset_0()
        .bg(hsla(0.0, 0.0, 0.0, 0.58))
        .flex()
        .items_center()
        .justify_center()
        .on_mouse_down(MouseButton::Left, backdrop_close)
        .child(
            div()
                .id("connection-modal-card")
                .w(px(440.0))
                .max_h(px(690.0))
                .rounded(px(10.0))
                .bg(palette.modal_bg)
                .border_1()
                .border_color(palette.card_border)
                .shadow_2xl()
                .flex()
                .flex_col()
                .overflow_hidden()
                .on_mouse_down(MouseButton::Left, |_, _, cx| {
                    cx.stop_propagation();
                })
                // Header: clean WinUI ContentDialog title
                .child(
                    div().px_6().pt_5().pb_3().child(
                        div()
                            .text_size(px(19.0))
                            .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                            .text_color(palette.text_primary)
                            .child("Workspace settings"),
                    ),
                )
                // Scrollable body
                .child(
                    div()
                        .id("connection-modal-body")
                        .flex_1()
                        .min_h_0()
                        .overflow_y_scroll()
                        .px_6()
                        .pb_4()
                        .child(
                            v_flex()
                                .gap_3p5()
                                // Identity row: [Icon/Monogram ⌄] [Workspace name ×] [Color dot]
                                .child(
                                    h_flex()
                                        .items_center()
                                        .gap_2p5()
                                        // Icon / monogram picker trigger
                                        .child(
                                            div()
                                                .id("modal-icon-picker-btn")
                                                .relative()
                                                .size(px(40.0))
                                                .flex_shrink_0()
                                                .rounded(px(8.0))
                                                .bg(swatch_color.opacity(0.18))
                                                .border_1()
                                                .border_color(swatch_color.opacity(0.65))
                                                .flex()
                                                .items_center()
                                                .justify_center()
                                                .cursor_pointer()
                                                .hover(|s| s.bg(swatch_color.opacity(0.28)))
                                                .on_click(act(
                                                    ConnectionModalAction::ToggleIconPicker,
                                                ))
                                                .child(if let Some(icon) = selected_icon {
                                                    Icon::new(icon)
                                                        .size(px(17.0))
                                                        .text_color(palette.text_primary)
                                                        .into_any_element()
                                                } else {
                                                    div()
                                                        .text_size(px(14.0))
                                                        .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                                        .text_color(palette.text_primary)
                                                        .child(monogram.clone())
                                                        .into_any_element()
                                                })
                                                .child(
                                                    div()
                                                        .absolute()
                                                        .bottom(px(3.0))
                                                        .right(px(3.0))
                                                        .child(
                                                            Icon::new(IconName::ChevronDown)
                                                                .size(px(8.5))
                                                                .text_color(palette.text_secondary),
                                                        ),
                                                ),
                                        )
                                        // Workspace name input with inline clear button
                                        .child(
                                            h_flex()
                                                .flex_1()
                                                .min_w_0()
                                                .h(px(36.0))
                                                .px_2p5()
                                                .rounded(px(6.0))
                                                .bg(palette.card_bg)
                                                .border_1()
                                                .border_color(palette.card_border)
                                                .items_center()
                                                .gap_1()
                                                .child(
                                                    div().flex_1().min_w_0().child(
                                                        Input::new(&modal.name_input)
                                                            .small()
                                                            .appearance(false),
                                                    ),
                                                )
                                                .when(name_non_empty, |el| {
                                                    el.child(
                                                        div()
                                                            .id("modal-name-clear-btn")
                                                            .size(px(22.0))
                                                            .rounded(px(4.0))
                                                            .flex()
                                                            .items_center()
                                                            .justify_center()
                                                            .cursor_pointer()
                                                            .hover(|s| s.bg(palette.subtle_hover))
                                                            .on_click(act(
                                                                ConnectionModalAction::ClearName,
                                                            ))
                                                            .child(
                                                                Icon::new(IconName::X)
                                                                    .size(px(12.0))
                                                                    .text_color(palette.text_muted),
                                                            ),
                                                    )
                                                }),
                                        )
                                        // Color swatch trigger
                                        .child(
                                            div()
                                                .id("modal-color-picker-btn")
                                                .size(px(36.0))
                                                .flex_shrink_0()
                                                .rounded(px(8.0))
                                                .flex()
                                                .items_center()
                                                .justify_center()
                                                .cursor_pointer()
                                                .hover(|s| s.bg(palette.subtle_hover))
                                                .on_click(act(
                                                    ConnectionModalAction::ToggleColorPicker,
                                                ))
                                                .child(
                                                    div()
                                                        .size(px(20.0))
                                                        .rounded_full()
                                                        .bg(swatch_color)
                                                        .border_1()
                                                        .border_color(palette.card_border),
                                                ),
                                        ),
                                )
                                // Expandable Icon Picker panel
                                .when(modal.show_icon_picker, |el| {
                                    let on_act_grid = on_action.clone();
                                    let on_act_letter = on_action.clone();
                                    el.child(
                                        v_flex()
                                            .p_3()
                                            .gap_2p5()
                                            .rounded(px(8.0))
                                            .bg(palette.panel_bg)
                                            .border_1()
                                            .border_color(palette.card_border)
                                            .child(
                                                div()
                                                    .text_size(px(11.0))
                                                    .font_weight(gpui_kit::FontWeight::MEDIUM)
                                                    .text_color(palette.text_secondary)
                                                    .child("Workspace icon"),
                                            )
                                            .child(h_flex().flex_wrap().gap_1p5().children(
                                                WORKSPACE_ICONS.iter().map(|(id, _label, icon)| {
                                                    let is_sel = modal
                                                        .icon
                                                        .as_deref()
                                                        .is_some_and(|k| k.eq_ignore_ascii_case(id));
                                                    let key = (*id).to_string();
                                                    let cb = on_act_grid.clone();
                                                    div()
                                                        .id(SharedString::from(format!(
                                                            "ws-icon-{id}"
                                                        )))
                                                        .size(px(32.0))
                                                        .rounded(px(6.0))
                                                        .flex()
                                                        .items_center()
                                                        .justify_center()
                                                        .cursor_pointer()
                                                        .when(is_sel, |d| {
                                                            d.bg(swatch_color.opacity(0.22))
                                                                .border_1()
                                                                .border_color(swatch_color)
                                                        })
                                                        .when(!is_sel, |d| {
                                                            d.hover(|s| s.bg(palette.subtle_hover))
                                                        })
                                                        .on_click(cx.listener(
                                                            move |this, _, window, cx| {
                                                                cb(
                                                                    this,
                                                                    ConnectionModalAction::PickIcon(
                                                                        Some(key.clone()),
                                                                    ),
                                                                    window,
                                                                    cx,
                                                                );
                                                            },
                                                        ))
                                                        .child(
                                                            Icon::new(*icon)
                                                                .size(px(15.0))
                                                                .text_color(palette.text_primary),
                                                        )
                                                }),
                                            ))
                                            .child(
                                                h_flex()
                                                    .id("ws-icon-use-letter")
                                                    .px_2p5()
                                                    .py_1p5()
                                                    .rounded(px(6.0))
                                                    .items_center()
                                                    .gap_2()
                                                    .cursor_pointer()
                                                    .when(modal.icon.is_none(), |d| {
                                                        d.bg(swatch_color.opacity(0.16))
                                                    })
                                                    .hover(|s| s.bg(palette.subtle_hover))
                                                    .on_click(cx.listener(
                                                        move |this, _, window, cx| {
                                                            on_act_letter(
                                                                this,
                                                                ConnectionModalAction::PickIcon(
                                                                    None,
                                                                ),
                                                                window,
                                                                cx,
                                                            );
                                                        },
                                                    ))
                                                    .child(
                                                        div()
                                                            .text_xs()
                                                            .font_weight(
                                                                gpui_kit::FontWeight::SEMIBOLD,
                                                            )
                                                            .text_color(swatch_color)
                                                            .child(monogram.clone()),
                                                    )
                                                    .child(
                                                        div()
                                                            .text_xs()
                                                            .text_color(palette.text_primary)
                                                            .child("Use the first letter"),
                                                    ),
                                            ),
                                    )
                                })
                                // Expandable Color Picker panel
                                .when(modal.show_color_picker, |el| {
                                    let on_act_color = on_action.clone();
                                    el.child(
                                        v_flex()
                                            .p_3()
                                            .gap_2()
                                            .rounded(px(8.0))
                                            .bg(palette.panel_bg)
                                            .border_1()
                                            .border_color(palette.card_border)
                                            .child(
                                                div()
                                                    .text_size(px(11.0))
                                                    .font_weight(gpui_kit::FontWeight::MEDIUM)
                                                    .text_color(palette.text_secondary)
                                                    .child("Workspace colour"),
                                            )
                                            .child(h_flex().flex_wrap().gap_2().children(
                                                SPACE_COLORS.iter().map(|(label, hex)| {
                                                    let selected =
                                                        modal.color.eq_ignore_ascii_case(hex);
                                                    let hex_owned = (*hex).to_string();
                                                    let swatch = parse_hex_color(hex);
                                                    let cb = on_act_color.clone();
                                                    div()
                                                        .id(SharedString::from(format!(
                                                            "ws-swatch-{label}"
                                                        )))
                                                        .size(px(26.0))
                                                        .rounded_full()
                                                        .cursor_pointer()
                                                        .flex()
                                                        .items_center()
                                                        .justify_center()
                                                        .when(selected, |d| {
                                                            d.border_2()
                                                                .border_color(palette.text_primary)
                                                        })
                                                        .on_click(cx.listener(
                                                            move |this, _, window, cx| {
                                                                cb(
                                                                    this,
                                                                    ConnectionModalAction::PickColor(
                                                                        hex_owned.clone(),
                                                                    ),
                                                                    window,
                                                                    cx,
                                                                );
                                                            },
                                                        ))
                                                        .child(
                                                            div()
                                                                .size(px(18.0))
                                                                .rounded_full()
                                                                .bg(swatch),
                                                        )
                                                }),
                                            )),
                                    )
                                })
                                // Database provider dropdown
                                .child(
                                    v_flex()
                                        .gap_1p5()
                                        .child(
                                            div()
                                                .text_size(px(12.0))
                                                .text_color(palette.text_secondary)
                                                .child("Database"),
                                        )
                                        .child(
                                            h_flex()
                                                .id("modal-provider-dropdown-btn")
                                                .h(px(36.0))
                                                .px_3()
                                                .rounded(px(6.0))
                                                .bg(palette.card_bg)
                                                .border_1()
                                                .border_color(palette.card_border)
                                                .items_center()
                                                .justify_between()
                                                .cursor_pointer()
                                                .hover(|s| s.bg(palette.subtle_hover))
                                                .on_click(act(
                                                    ConnectionModalAction::ToggleProviderDropdown,
                                                ))
                                                .child(
                                                    div()
                                                        .text_size(px(13.0))
                                                        .text_color(palette.text_primary)
                                                        .child(active_provider_name),
                                                )
                                                .child(
                                                    Icon::new(if modal.show_provider_dropdown {
                                                        IconName::ChevronUp
                                                    } else {
                                                        IconName::ChevronDown
                                                    })
                                                    .size(px(13.0))
                                                    .text_color(palette.text_muted),
                                                ),
                                        )
                                        .when(modal.show_provider_dropdown, |el| {
                                            let on_act_prov = on_action.clone();
                                            el.child(
                                                v_flex()
                                                    .p_1()
                                                    .gap_0p5()
                                                    .rounded(px(6.0))
                                                    .bg(palette.panel_bg)
                                                    .border_1()
                                                    .border_color(palette.card_border)
                                                    .children(providers.into_iter().map(|prov| {
                                                        let pid = prov.id().to_string();
                                                        let is_sel = modal
                                                            .provider_id
                                                            .eq_ignore_ascii_case(&pid);
                                                        let cb = on_act_prov.clone();
                                                        h_flex()
                                                            .id(SharedString::from(format!(
                                                                "prov-opt-{pid}"
                                                            )))
                                                            .px_2p5()
                                                            .py_1p5()
                                                            .rounded(px(5.0))
                                                            .items_center()
                                                            .justify_between()
                                                            .cursor_pointer()
                                                            .when(is_sel, |d| {
                                                                d.bg(palette.subtle_hover)
                                                            })
                                                            .hover(|s| s.bg(palette.subtle_hover))
                                                            .on_click(cx.listener(
                                                                move |this, _, window, cx| {
                                                                    cb(
                                                                        this,
                                                                        ConnectionModalAction::SwitchProvider(
                                                                            pid.clone(),
                                                                        ),
                                                                        window,
                                                                        cx,
                                                                    );
                                                                },
                                                            ))
                                                            .child(
                                                                div()
                                                                    .text_size(px(12.5))
                                                                    .font_weight(if is_sel {
                                                                        gpui_kit::FontWeight::MEDIUM
                                                                    } else {
                                                                        gpui_kit::FontWeight::NORMAL
                                                                    })
                                                                    .text_color(palette.text_primary)
                                                                    .child(prov.display_name()),
                                                            )
                                                            .when(is_sel, |d| {
                                                                d.child(
                                                                    Icon::new(IconName::Check)
                                                                        .size(px(13.0))
                                                                        .text_color(palette.accent),
                                                                )
                                                            })
                                                    })),
                                            )
                                        }),
                                )
                                // Primary ProviderSettings (Server, Initial database, User, Password)
                                .children({
                                    let mut items: Vec<AnyElement> = Vec::new();
                                    for setting in basic_settings {
                                        items.push(render_setting_field(
                                            modal,
                                            setting,
                                            false,
                                            palette,
                                            cx,
                                            on_action.clone(),
                                        ));
                                    }
                                    items
                                })
                                // Collapsible "Advanced connection settings >" card button
                                .when(!advanced_settings.is_empty(), |el| {
                                    let show_adv = modal.show_advanced;
                                    let mut adv_items: Vec<AnyElement> = Vec::new();
                                    if show_adv {
                                        for setting in advanced_settings {
                                            adv_items.push(render_setting_field(
                                                modal,
                                                setting,
                                                true,
                                                palette,
                                                cx,
                                                on_action.clone(),
                                            ));
                                        }
                                    }
                                    el.child(
                                        v_flex()
                                            .gap_2p5()
                                            .pt_0p5()
                                            .child(
                                                h_flex()
                                                    .id("toggle-advanced-settings")
                                                    .h(px(38.0))
                                                    .px_3p5()
                                                    .rounded(px(6.0))
                                                    .bg(palette.card_bg)
                                                    .border_1()
                                                    .border_color(palette.card_border)
                                                    .items_center()
                                                    .justify_between()
                                                    .cursor_pointer()
                                                    .hover(|s| s.bg(palette.subtle_hover))
                                                    .on_click(act(
                                                        ConnectionModalAction::ToggleAdvanced,
                                                    ))
                                                    .child(
                                                        div()
                                                            .text_size(px(13.0))
                                                            .text_color(palette.text_primary)
                                                            .child("Advanced connection settings"),
                                                    )
                                                    .child(
                                                        Icon::new(if show_adv {
                                                            IconName::ChevronDown
                                                        } else {
                                                            IconName::ChevronRight
                                                        })
                                                        .size(px(13.0))
                                                        .text_color(palette.text_muted),
                                                    ),
                                            )
                                            .when(show_adv, |el| {
                                                el.child(
                                                    v_flex()
                                                        .p_3p5()
                                                        .gap_3()
                                                        .rounded(px(8.0))
                                                        .bg(palette.panel_bg)
                                                        .border_1()
                                                        .border_color(palette.card_border)
                                                        .children(adv_items),
                                                )
                                            }),
                                    )
                                })
                                // Live provider transport warning (amber text matching WinUI)
                                .when_some(transport_warning, |el, warning| {
                                    el.child(
                                        div()
                                            .pt_0p5()
                                            .text_size(px(11.5))
                                            .text_color(palette.warning)
                                            .child(warning),
                                    )
                                })
                                // Inline test feedback
                                .when_some(modal.test_status.clone(), |el, status| {
                                    let (ok, msg) = match status {
                                        Ok(m) => (true, m),
                                        Err(e) => (false, e),
                                    };
                                    let tone = if ok { palette.success } else { palette.danger };
                                    el.child(
                                        h_flex()
                                            .px_3()
                                            .py_2()
                                            .rounded(px(6.0))
                                            .bg(tone.opacity(0.12))
                                            .border_1()
                                            .border_color(tone.opacity(0.35))
                                            .gap_2()
                                            .items_center()
                                            .child(
                                                Icon::new(if ok {
                                                    IconName::CircleCheck
                                                } else {
                                                    IconName::CircleAlert
                                                })
                                                .size(px(14.0))
                                                .text_color(tone),
                                            )
                                            .child(
                                                div()
                                                    .flex_1()
                                                    .text_xs()
                                                    .text_color(cx.theme().foreground)
                                                    .child(msg),
                                            ),
                                    )
                                })
                                // Subtle destructive delete section at bottom of scroll body (WinUI layout)
                                .when(modal.can_delete, |el| {
                                    let confirming = modal.confirming_delete;
                                    el.child(
                                        v_flex()
                                            .mt_2()
                                            .pt_3()
                                            .border_t_1()
                                            .border_color(palette.hairline_subtle)
                                            .gap_1p5()
                                            .child(if confirming {
                                                h_flex()
                                                    .items_center()
                                                    .justify_between()
                                                    .gap_2()
                                                    .child(
                                                        div()
                                                            .text_xs()
                                                            .text_color(palette.danger)
                                                            .child("Delete this workspace?"),
                                                    )
                                                    .child(
                                                        h_flex()
                                                            .gap_1p5()
                                                            .child(
                                                                div()
                                                                    .id("modal-del-cancel")
                                                                    .px_2p5()
                                                                    .py_1()
                                                                    .rounded(px(5.0))
                                                                    .text_xs()
                                                                    .text_color(
                                                                        palette.text_secondary,
                                                                    )
                                                                    .cursor_pointer()
                                                                    .hover(|s| {
                                                                        s.bg(palette.subtle_hover)
                                                                    })
                                                                    .on_click(act(
                                                                        ConnectionModalAction::SetConfirmDelete(false),
                                                                    ))
                                                                    .child("Keep"),
                                                            )
                                                            .child(
                                                                div()
                                                                    .id("modal-del-confirm")
                                                                    .px_2p5()
                                                                    .py_1()
                                                                    .rounded(px(5.0))
                                                                    .bg(palette.danger)
                                                                    .text_xs()
                                                                    .font_weight(
                                                                        gpui_kit::FontWeight::SEMIBOLD,
                                                                    )
                                                                    .text_color(rgb(0xFFFFFF))
                                                                    .cursor_pointer()
                                                                    .on_click(act(
                                                                        ConnectionModalAction::ConfirmDelete,
                                                                    ))
                                                                    .child("Delete workspace"),
                                                            ),
                                                    )
                                                    .into_any_element()
                                            } else {
                                                h_flex()
                                                    .id("modal-del-arm")
                                                    .px_2()
                                                    .py_1()
                                                    .rounded(px(5.0))
                                                    .items_center()
                                                    .gap_2()
                                                    .cursor_pointer()
                                                    .hover(|s| s.bg(palette.danger.opacity(0.12)))
                                                    .on_click(act(
                                                        ConnectionModalAction::SetConfirmDelete(
                                                            true,
                                                        ),
                                                    ))
                                                    .child(
                                                        Icon::new(IconName::Trash)
                                                            .size(px(12.5))
                                                            .text_color(palette.danger),
                                                    )
                                                    .child(
                                                        div()
                                                            .text_size(px(12.0))
                                                            .text_color(palette.danger)
                                                            .child("Delete workspace"),
                                                    )
                                                    .into_any_element()
                                            })
                                            .child(
                                                div()
                                                    .text_size(px(10.5))
                                                    .text_color(palette.text_muted)
                                                    .child("Tabs with SQL move to Archive. This cannot be undone."),
                                            ),
                                    )
                                }),
                        ),
                )
                // Footer: 3 equal WinUI ContentDialog buttons — Save (coral) | Test | Cancel
                .child(
                    h_flex()
                        .px_6()
                        .py_4()
                        .bg(palette.subtle_fill.opacity(0.35))
                        .border_t_1()
                        .border_color(palette.hairline)
                        .gap_2p5()
                        .child(
                            div()
                                .id("modal-save-connect")
                                .flex_1()
                                .h(px(34.0))
                                .rounded(px(6.0))
                                .bg(coral_bg)
                                .hover(|s| s.bg(coral_hover))
                                .flex()
                                .items_center()
                                .justify_center()
                                .cursor_pointer()
                                .text_size(px(13.0))
                                .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                .text_color(coral_fg)
                                .on_click(act(ConnectionModalAction::Save))
                                .child("Save"),
                        )
                        .child(
                            div()
                                .id("modal-test-conn")
                                .flex_1()
                                .h(px(34.0))
                                .rounded(px(6.0))
                                .bg(palette.card_bg)
                                .border_1()
                                .border_color(palette.card_border)
                                .hover(|s| s.bg(palette.subtle_hover))
                                .flex()
                                .items_center()
                                .justify_center()
                                .cursor_pointer()
                                .text_size(px(13.0))
                                .text_color(palette.text_primary)
                                .when(modal.testing, |d| d.opacity(0.65))
                                .on_click(act(ConnectionModalAction::Test))
                                .child(if modal.testing { "Testing…" } else { "Test" }),
                        )
                        .child(
                            div()
                                .id("modal-cancel")
                                .flex_1()
                                .h(px(34.0))
                                .rounded(px(6.0))
                                .bg(palette.card_bg)
                                .border_1()
                                .border_color(palette.card_border)
                                .hover(|s| s.bg(palette.subtle_hover))
                                .flex()
                                .items_center()
                                .justify_center()
                                .cursor_pointer()
                                .text_size(px(13.0))
                                .text_color(palette.text_primary)
                                .on_click(act(ConnectionModalAction::Close))
                                .child("Cancel"),
                        ),
                ),
        )
}

fn render_setting_field<V: 'static>(
    modal: &ConnectionModal,
    setting: &larik_data::ProviderSetting,
    show_hint: bool,
    palette: StudioPalette,
    cx: &Context<V>,
    on_action: impl Fn(&mut V, ConnectionModalAction, &mut Window, &mut Context<V>) + 'static,
) -> AnyElement {
    let key_owned = setting.key.to_string();

    if setting.kind == ProviderSettingKind::Boolean {
        let checked = modal.bool_values.get(setting.key).copied().unwrap_or(false);
        return v_flex()
            .gap_1()
            .child(
                h_flex()
                    .items_center()
                    .justify_between()
                    .child(
                        div()
                            .text_size(px(12.0))
                            .text_color(palette.text_primary)
                            .child(setting.label),
                    )
                    .child(
                        Switch::new(SharedString::from(format!("sw-{}", setting.key)))
                            .checked(checked)
                            .on_click(cx.listener(move |this, new_val: &bool, window, cx| {
                                on_action(
                                    this,
                                    ConnectionModalAction::ToggleBool(key_owned.clone(), *new_val),
                                    window,
                                    cx,
                                );
                            })),
                    ),
            )
            .when_some(setting.hint.filter(|_| show_hint), |el, hint| {
                el.child(
                    div()
                        .text_size(px(10.5))
                        .text_color(palette.text_muted)
                        .child(hint),
                )
            })
            .into_any_element();
    }

    let input_el = modal.text_inputs.get(setting.key).map(|entity| {
        let input = Input::new(entity).small();
        if setting.kind == ProviderSettingKind::Password {
            input.mask_toggle()
        } else {
            input
        }
    });

    v_flex()
        .gap_1p5()
        .child(
            div()
                .text_size(px(12.0))
                .text_color(palette.text_secondary)
                .child(setting.label),
        )
        .when_some(input_el, |el, input| el.child(input))
        .when_some(setting.hint.filter(|_| show_hint), |el, hint| {
            el.child(
                div()
                    .text_size(px(10.5))
                    .text_color(palette.text_muted)
                    .child(hint),
            )
        })
        .into_any_element()
}
