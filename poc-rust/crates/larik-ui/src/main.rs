mod completion;
mod connection_dialog;
mod results_table;
mod studio;
mod theme;

use std::sync::Arc;

use gpui_kit::assets::AllAssets;
use gpui_kit::{AppContext as _, Bounds, TitlebarOptions, WindowBounds, WindowOptions, px, size};
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
                titlebar: Some(TitlebarOptions {
                    title: Some("Larik — Rust GPUI PoC".into()),
                    ..Default::default()
                }),
                ..Default::default()
            };

            gpui_kit::open_window(options, cx, |window, cx| {
                window.set_window_title("Larik — Rust GPUI PoC");
                cx.new(|cx| {
                    StudioView::new(catalog.clone(), store, sessions.clone(), window, cx)
                })
            })
            .expect("failed to open Larik PoC window");

            cx.activate(true);
        });
}
