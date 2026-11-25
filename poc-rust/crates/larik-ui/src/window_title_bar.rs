use std::sync::Arc;

use gpui_kit::assets::IconName;
use gpui_kit::base::InteractiveElementExt as _;
use gpui_kit::component::theme::ActiveTheme;
use gpui_kit::component::{Icon, Sizable as _, TITLE_BAR_HEIGHT, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::{
    AnyElement, App, ClickEvent, Image, InteractiveElement as _, IntoElement, MouseButton,
    ParentElement as _, SharedString, StatefulInteractiveElement as _, Styled as _, Window,
    WindowControlArea, div, img, px, transparent_black,
};

use crate::theme::StudioPalette;

/// Renders a Windows 11 native-feeling transparent title bar where the leading
/// action button (sidebar toggle or back arrow) sits **outside** the
/// `WindowControlArea::Drag` hit-test region.
///
/// Keeping interactive buttons outside `WindowControlArea::Drag` ensures Windows
/// `WM_NCHITTEST` returns `HTCLIENT` for them instead of `HTCAPTION`, so clicks,
/// hovers, and tooltips fire reliably without Windows swallowing mouse-up into
/// the caption drag loop, while the remaining title/empty area still drags,
/// snaps, and double-click-maximizes natively.
#[allow(clippy::too_many_arguments)]
pub fn render_app_title_bar(
    id_prefix: &'static str,
    leading_icon: IconName,
    app_icon: &Arc<Image>,
    title: impl Into<SharedString>,
    palette: StudioPalette,
    window: &Window,
    cx: &App,
    on_leading_click: impl Fn(&ClickEvent, &mut Window, &mut App) + 'static,
) -> AnyElement {
    let title: SharedString = title.into();
    let is_windows = cfg!(target_os = "windows");
    let is_macos = cfg!(target_os = "macos");
    let is_linux = cfg!(target_os = "linux");

    let supported = window.window_controls();
    let is_maximized = window.is_maximized();

    let hover_bg = cx.theme().secondary_hover;
    let active_bg = cx.theme().secondary_active;
    let hover_fg = cx.theme().secondary_foreground;
    let danger_bg = cx.theme().danger;
    let danger_active_bg = cx.theme().danger_active;
    let danger_fg = cx.theme().danger_foreground;

    h_flex()
        .id(SharedString::from(format!("{id_prefix}-titlebar")))
        .w_full()
        .h(TITLE_BAR_HEIGHT)
        .flex_shrink_0()
        .items_center()
        .bg(transparent_black())
        .when(is_macos, |el| el.pl(px(76.0)))
        .when(!is_macos, |el| el.pl(px(6.0)))
        // Interactive leading button — intentionally OUTSIDE WindowControlArea::Drag
        .child(
            div()
                .id(SharedString::from(format!("{id_prefix}-leading-btn")))
                .size(px(28.0))
                .flex_shrink_0()
                .rounded(px(5.0))
                .flex()
                .items_center()
                .justify_center()
                .cursor_pointer()
                .hover(|s| s.bg(palette.subtle_hover))
                .active(|s| s.bg(palette.subtle_fill))
                .on_mouse_down(MouseButton::Left, |_, window, cx| {
                    window.prevent_default();
                    cx.stop_propagation();
                })
                .on_click(on_leading_click)
                .child(
                    Icon::new(leading_icon)
                        .size(px(14.0))
                        .text_color(palette.text_secondary),
                ),
        )
        // Draggable title / empty space — Windows HTCAPTION lives ONLY here
        .child(
            h_flex()
                .id(SharedString::from(format!("{id_prefix}-drag-region")))
                .flex_1()
                .min_w_0()
                .h_full()
                .pl_2()
                .items_center()
                .gap_2()
                .window_control_area(WindowControlArea::Drag)
                .when(is_macos, |el| {
                    el.on_double_click(|_, window, _| window.titlebar_double_click())
                })
                .when(is_linux, |el| {
                    el.on_double_click(|_, window, _| window.zoom_window())
                })
                .child(img(app_icon.clone()).size(px(18.0)).rounded(px(4.0)))
                .child(
                    div()
                        .text_size(px(12.0))
                        .font_weight(gpui_kit::FontWeight::MEDIUM)
                        .text_color(palette.text_primary)
                        .truncate()
                        .child(title),
                ),
        )
        // Native Windows / Linux caption controls (Min / Max / Close)
        .when(!is_macos, |el| {
            el.child(
                h_flex()
                    .id(SharedString::from(format!("{id_prefix}-window-controls")))
                    .h_full()
                    .flex_shrink_0()
                    .items_center()
                    .when(supported.minimize, |controls| {
                        controls.child(
                            div()
                                .id(SharedString::from(format!("{id_prefix}-win-min")))
                                .w(px(42.0))
                                .h_full()
                                .flex()
                                .items_center()
                                .justify_center()
                                .text_color(palette.text_secondary)
                                .hover(|s| s.bg(hover_bg).text_color(hover_fg))
                                .active(|s| s.bg(active_bg).text_color(hover_fg))
                                .when(is_windows, |b| b.window_control_area(WindowControlArea::Min))
                                .when(is_linux, |b| {
                                    b.on_click(|_, window, cx| {
                                        cx.stop_propagation();
                                        window.minimize_window();
                                    })
                                })
                                .child(Icon::new(IconName::WindowMinimize).small()),
                        )
                    })
                    .when(supported.maximize, |controls| {
                        controls.child(
                            div()
                                .id(SharedString::from(format!("{id_prefix}-win-max")))
                                .w(px(42.0))
                                .h_full()
                                .flex()
                                .items_center()
                                .justify_center()
                                .text_color(palette.text_secondary)
                                .hover(|s| s.bg(hover_bg).text_color(hover_fg))
                                .active(|s| s.bg(active_bg).text_color(hover_fg))
                                .when(is_windows, |b| b.window_control_area(WindowControlArea::Max))
                                .when(is_linux, |b| {
                                    b.on_click(|_, window, cx| {
                                        cx.stop_propagation();
                                        window.zoom_window();
                                    })
                                })
                                .child(
                                    Icon::new(if is_maximized {
                                        IconName::WindowRestore
                                    } else {
                                        IconName::WindowMaximize
                                    })
                                    .small(),
                                ),
                        )
                    })
                    .child(
                        div()
                            .id(SharedString::from(format!("{id_prefix}-win-close")))
                            .w(px(42.0))
                            .h_full()
                            .flex()
                            .items_center()
                            .justify_center()
                            .text_color(palette.text_secondary)
                            .hover(|s| s.bg(danger_bg).text_color(danger_fg))
                            .active(|s| s.bg(danger_active_bg).text_color(danger_fg))
                            .when(is_windows, |b| {
                                b.window_control_area(WindowControlArea::Close)
                            })
                            .when(is_linux, |b| {
                                b.on_click(|_, window, cx| {
                                    cx.stop_propagation();
                                    window.remove_window();
                                })
                            })
                            .child(Icon::new(IconName::WindowClose).small()),
                    ),
            )
        })
        .into_any_element()
}
