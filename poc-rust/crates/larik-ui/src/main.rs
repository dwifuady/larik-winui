mod completion;
mod connection_dialog;
mod results_table;
mod studio;
mod theme;

use std::sync::Arc;

use gpui_kit::assets::AllAssets;
use gpui_kit::component::TitleBar;
use gpui_kit::{
    AppContext as _, Bounds, WindowBackgroundAppearance, WindowBounds, WindowOptions, px, size,
};
use larik_core::session::SessionManager;
use larik_core::store::StudioStore;
use larik_data::ProviderCatalog;
use larik_provider_demo::DemoProvider;
use larik_provider_mssql::SqlServerProvider;

use crate::studio::{StudioView, bind_studio_keys};

fn main() {
    let mut catalog = ProviderCatalog::new();
    catalog.register(Arc::new(DemoProvider));
    catalog.register(Arc::new(SqlServerProvider));

    let store = StudioStore::open_default().expect("failed to initialize PoC SQLite store");
    let sessions =
        Arc::new(SessionManager::new(catalog.clone()).expect("failed to start Tokio runtime"));

    gpui_kit::application()
        .with_assets(AllAssets)
        .run(move |cx| {
            gpui_kit::init(cx);
            bind_studio_keys(cx);

            let bounds = Bounds::centered(None, size(px(1360.0), px(860.0)), cx);
            let options = WindowOptions {
                window_bounds: Some(WindowBounds::Windowed(bounds)),
                window_min_size: Some(size(px(900.0), px(600.0))),
                window_background: WindowBackgroundAppearance::MicaBackdrop,
                ..TitleBar::window_options()
            };

            gpui_kit::open_window(options, cx, |window, cx| {
                window.set_window_title("Larik SQL Studio");
                cx.new(|cx| {
                    StudioView::new(catalog.clone(), store, sessions.clone(), window, cx)
                })
            })
            .expect("failed to open Larik PoC window");

            cx.activate(true);
        });
}
