use std::collections::HashMap;
use std::sync::Arc;

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{Input, InputState};
use gpui_kit::component::switch::Switch;
use gpui_kit::component::theme::ActiveTheme;
use gpui_kit::component::{Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::{
    AnyElement, App, AppContext as _, Context, Entity, InteractiveElement as _, IntoElement,
    MouseButton, ParentElement as _, SharedString, StatefulInteractiveElement as _, Styled as _,
    Window, div, hsla, px,
};
use larik_core::SPACE_COLORS;
use larik_data::{ConnectionProfile, DatabaseProvider, ProviderCatalog, ProviderSettingKind};

use crate::theme::{StudioPalette, parse_hex_color};

/// State backing the provider-generated Connection Settings modal.
pub struct ConnectionModal {
    pub space_id: String,
    pub name_input: Entity<InputState>,
    pub color: String,
    pub provider_id: String,
    pub text_inputs: HashMap<String, Entity<InputState>>,
    pub bool_values: HashMap<String, bool>,
    pub show_advanced: bool,
    pub testing: bool,
    pub test_status: Option<Result<String, String>>,
}

impl ConnectionModal {
    pub fn open(
        space_id: String,
        space_name: &str,
        space_color: &str,
        profile: &ConnectionProfile,
        catalog: &ProviderCatalog,
        window: &mut Window,
        cx: &mut App,
    ) -> Self {
        let name_input = cx.new(|cx| {
            InputState::new(window, cx)
                .placeholder("Workspace name")
                .default_value(space_name.to_string())
        });

        let mut modal = Self {
            space_id,
            name_input,
            color: space_color.to_string(),
            provider_id: profile.provider_id.clone(),
            text_inputs: HashMap::new(),
            bool_values: HashMap::new(),
            show_advanced: false,
            testing: false,
            test_status: None,
        };
        modal.rebuild_fields(profile, catalog, window, cx);
        modal
    }

    pub fn switch_provider(
        &mut self,
        provider_id: &str,
        catalog: &ProviderCatalog,
        window: &mut Window,
        cx: &mut App,
    ) {
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

    fn rebuild_fields(
        &mut self,
        profile: &ConnectionProfile,
        catalog: &ProviderCatalog,
        window: &mut Window,
        cx: &mut App,
    ) {
        self.text_inputs.clear();
        self.bool_values.clear();

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

/// Renders the centered connection configuration modal over the studio window.
#[allow(clippy::too_many_arguments)]
pub fn render_connection_modal<V: 'static>(
    modal: &ConnectionModal,
    catalog: &ProviderCatalog,
    palette: StudioPalette,
    cx: &mut Context<V>,
    on_close: impl Fn(&mut V, &mut Window, &mut Context<V>) + 'static,
    on_switch_provider: impl Fn(&mut V, String, &mut Window, &mut Context<V>) + 'static + Clone,
    on_pick_color: impl Fn(&mut V, String, &mut Window, &mut Context<V>) + 'static + Clone,
    on_toggle_bool: impl Fn(&mut V, String, bool, &mut Window, &mut Context<V>) + 'static + Clone,
    on_toggle_advanced: impl Fn(&mut V, &mut Window, &mut Context<V>) + 'static,
    on_test: impl Fn(&mut V, &mut Window, &mut Context<V>) + 'static,
    on_save_connect: impl Fn(&mut V, &mut Window, &mut Context<V>) + 'static,
) -> impl IntoElement {
    let providers: Vec<Arc<dyn DatabaseProvider>> = catalog.all().to_vec();
    let active_provider = catalog.find(&modal.provider_id);
    let basic_settings: Vec<_> = active_provider
        .as_ref()
        .map(|p| p.settings().iter().filter(|s| !s.is_advanced).collect())
        .unwrap_or_default();
    let advanced_settings: Vec<_> = active_provider
        .as_ref()
        .map(|p| p.settings().iter().filter(|s| s.is_advanced).collect())
        .unwrap_or_default();

    let close_backdrop = cx.listener(move |this, _, window, cx| on_close(this, window, cx));
    let on_test_click = cx.listener(move |this, _, window, cx| on_test(this, window, cx));
    let on_save_click = cx.listener(move |this, _, window, cx| on_save_connect(this, window, cx));
    let on_adv_click = cx.listener(move |this, _, window, cx| on_toggle_advanced(this, window, cx));

    div()
        .id("connection-modal-backdrop")
        .absolute()
        .inset_0()
        .bg(hsla(0.0, 0.0, 0.0, 0.56))
        .flex()
        .items_center()
        .justify_center()
        .on_mouse_down(MouseButton::Left, close_backdrop)
        .child(
            div()
                .id("connection-modal-card")
                .w(px(520.0))
                .max_h(px(680.0))
                .rounded(px(12.0))
                .bg(palette.rail_bg)
                .border_1()
                .border_color(palette.hairline)
                .shadow_xl()
                .flex()
                .flex_col()
                .overflow_hidden()
                .on_mouse_down(MouseButton::Left, |_, _, cx| {
                    cx.stop_propagation();
                })
                // Header
                .child(
                    h_flex()
                        .px_5()
                        .py_3p5()
                        .border_b_1()
                        .border_color(palette.hairline)
                        .items_center()
                        .justify_between()
                        .child(
                            v_flex()
                                .gap_0p5()
                                .child(
                                    div()
                                        .text_sm()
                                        .font_weight(gpui_kit::FontWeight::SEMIBOLD)
                                        .text_color(palette.text_primary)
                                        .child("Workspace Connection"),
                                )
                                .child(
                                    div()
                                        .text_xs()
                                        .text_color(palette.text_muted)
                                        .child("Fields are generated directly from the selected provider."),
                                ),
                        ),
                )
                // Body
                .child(
                    div()
                        .id("connection-modal-body")
                        .flex_1()
                        .overflow_y_scroll()
                        .px_5()
                        .py_4()
                        .child(
                            v_flex()
                                .gap_3p5()
                                // Workspace name & color
                                .child(
                                    v_flex()
                                        .gap_1p5()
                                        .child(
                                            div()
                                                .text_xs()
                                                .font_weight(gpui_kit::FontWeight::MEDIUM)
                                                .text_color(palette.text_secondary)
                                                .child("Workspace Name"),
                                        )
                                        .child(Input::new(&modal.name_input).small()),
                                )
                                .child(
                                    h_flex()
                                        .gap_2()
                                        .items_center()
                                        .child(
                                            div()
                                                .text_xs()
                                                .text_color(palette.text_secondary)
                                                .child("Identity"),
                                        )
                                        .children(SPACE_COLORS.iter().map(|(label, hex)| {
                                            let selected = modal.color.eq_ignore_ascii_case(hex);
                                            let hex_owned = (*hex).to_string();
                                            let handler = on_pick_color.clone();
                                            let swatch = parse_hex_color(hex);
                                            div()
                                                .id(SharedString::from(format!("swatch-{label}")))
                                                .size(px(20.0))
                                                .rounded_full()
                                                .cursor_pointer()
                                                .flex()
                                                .items_center()
                                                .justify_center()
                                                .when(selected, |el| {
                                                    el.border_2().border_color(palette.text_primary)
                                                })
                                                .on_click(cx.listener(move |this, _, window, cx| {
                                                    handler(this, hex_owned.clone(), window, cx);
                                                }))
                                                .child(
                                                    div()
                                                        .size(px(12.0))
                                                        .rounded_full()
                                                        .bg(swatch),
                                                )
                                        })),
                                )
                                // Provider picker pills
                                .child(
                                    v_flex()
                                        .gap_1p5()
                                        .child(
                                            div()
                                                .text_xs()
                                                .font_weight(gpui_kit::FontWeight::MEDIUM)
                                                .text_color(palette.text_secondary)
                                                .child("Provider"),
                                        )
                                        .child(h_flex().gap_2().children(providers.into_iter().map(
                                            |prov| {
                                                let pid = prov.id().to_string();
                                                let selected =
                                                    modal.provider_id.eq_ignore_ascii_case(&pid);
                                                let handler = on_switch_provider.clone();
                                                let btn = Button::new(SharedString::from(format!(
                                                    "prov-btn-{pid}"
                                                )))
                                                .small()
                                                .label(prov.display_name())
                                                .on_click(cx.listener(
                                                    move |this, _, window, cx| {
                                                        handler(this, pid.clone(), window, cx);
                                                    },
                                                ));
                                                if selected {
                                                    btn.primary()
                                                } else {
                                                    btn.outline()
                                                }
                                            },
                                        ))),
                                )
                                // Basic ProviderSettings
                                .children({
                                    let mut items: Vec<AnyElement> = Vec::new();
                                    for setting in basic_settings {
                                        items.push(render_setting_field(
                                            modal,
                                            setting,
                                            palette,
                                            cx,
                                            on_toggle_bool.clone(),
                                        ));
                                    }
                                    items
                                })
                                // Advanced toggle + settings
                                .when(!advanced_settings.is_empty(), |el| {
                                    let show_adv = modal.show_advanced;
                                    let mut adv_items: Vec<AnyElement> = Vec::new();
                                    if show_adv {
                                        for setting in advanced_settings {
                                            adv_items.push(render_setting_field(
                                                modal,
                                                setting,
                                                palette,
                                                cx,
                                                on_toggle_bool.clone(),
                                            ));
                                        }
                                    }
                                    el.child(
                                        v_flex()
                                            .gap_2p5()
                                            .pt_1()
                                            .child(
                                                Button::new("toggle-advanced-settings")
                                                    .ghost()
                                                    .xsmall()
                                                    .icon(if show_adv {
                                                        IconName::ChevronDown
                                                    } else {
                                                        IconName::ChevronRight
                                                    })
                                                    .label(if show_adv {
                                                        "Hide advanced options"
                                                    } else {
                                                        "Advanced options"
                                                    })
                                                    .on_click(on_adv_click),
                                            )
                                            .when(show_adv, |el| {
                                                el.child(
                                                    v_flex()
                                                        .gap_3()
                                                        .pl_3()
                                                        .border_l_2()
                                                        .border_color(palette.hairline)
                                                        .children(adv_items),
                                                )
                                            }),
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
                                }),
                        ),
                )
                // Footer
                .child(
                    h_flex()
                        .px_5()
                        .py_3()
                        .border_t_1()
                        .border_color(palette.hairline)
                        .items_center()
                        .justify_between()
                        .child(
                            Button::new("modal-test-conn")
                                .outline()
                                .small()
                                .loading(modal.testing)
                                .icon(IconName::Plug)
                                .label(if modal.testing {
                                    "Testing…"
                                } else {
                                    "Test connection"
                                })
                                .on_click(on_test_click),
                        )
                        .child(
                            h_flex()
                                .gap_2()
                                .child(
                                    Button::new("modal-save-connect")
                                        .primary()
                                        .small()
                                        .label("Save & Connect")
                                        .on_click(on_save_click),
                                ),
                        ),
                ),
        )
}

fn render_setting_field<V: 'static>(
    modal: &ConnectionModal,
    setting: &larik_data::ProviderSetting,
    palette: StudioPalette,
    cx: &mut Context<V>,
    on_toggle_bool: impl Fn(&mut V, String, bool, &mut Window, &mut Context<V>) + 'static,
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
                            .text_xs()
                            .font_weight(gpui_kit::FontWeight::MEDIUM)
                            .text_color(palette.text_secondary)
                            .child(setting.label),
                    )
                    .child(
                        Switch::new(SharedString::from(format!("sw-{}", setting.key)))
                            .checked(checked)
                            .on_click(cx.listener(move |this, new_val: &bool, window, cx| {
                                on_toggle_bool(this, key_owned.clone(), *new_val, window, cx);
                            })),
                    ),
            )
            .when_some(setting.hint, |el, hint| {
                el.child(div().text_xs().text_color(palette.text_muted).child(hint))
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
        .gap_1()
        .child(
            h_flex()
                .gap_1()
                .child(
                    div()
                        .text_xs()
                        .font_weight(gpui_kit::FontWeight::MEDIUM)
                        .text_color(palette.text_secondary)
                        .child(setting.label),
                )
                .when(setting.required, |el| {
                    el.child(div().text_xs().text_color(palette.danger).child("*"))
                }),
        )
        .when_some(input_el, |el, input| el.child(input))
        .when_some(setting.hint, |el, hint| {
            el.child(div().text_xs().text_color(palette.text_muted).child(hint))
        })
        .into_any_element()
}
