use gpui_kit::component::theme::{Theme, ThemeMode};
use gpui_kit::{App, Hsla, Window, hsla, rgb};
use larik_core::ThemePreference;

/// Larik semantic colors ported directly from `Larik/Themes/StudioTheme.xaml`.
#[derive(Debug, Clone, Copy)]
pub struct StudioPalette {
    pub background: Hsla,
    pub rail_bg: Hsla,
    pub elevated: Hsla,
    pub hairline: Hsla,
    pub hairline_subtle: Hsla,
    pub row_alt: Hsla,
    pub text_primary: Hsla,
    pub text_secondary: Hsla,
    pub text_muted: Hsla,
    pub accent: Hsla,
    pub success: Hsla,
    pub warning: Hsla,
    pub danger: Hsla,
}

impl StudioPalette {
    pub fn for_mode(mode: ThemePreference) -> Self {
        match mode {
            ThemePreference::Dark => Self {
                background: rgb(0x161618).into(),
                rail_bg: rgb(0x1C1C1E).into(),
                elevated: rgb(0x232326).into(),
                hairline: hsla(0.0, 0.0, 1.0, 0.09),
                hairline_subtle: hsla(0.0, 0.0, 1.0, 0.05),
                row_alt: hsla(0.0, 0.0, 1.0, 0.03),
                text_primary: rgb(0xF5F5F7).into(),
                text_secondary: hsla(0.0, 0.0, 1.0, 0.72),
                text_muted: hsla(0.0, 0.0, 1.0, 0.42),
                accent: rgb(0x6366F1).into(),
                success: rgb(0x22C55E).into(),
                warning: rgb(0xF59E0B).into(),
                danger: rgb(0xEF4444).into(),
            },
            ThemePreference::Light => Self {
                background: rgb(0xFFFFFF).into(),
                rail_bg: rgb(0xF8F8F8).into(),
                elevated: rgb(0xF0F0F2).into(),
                hairline: hsla(0.0, 0.0, 0.0, 0.11),
                hairline_subtle: hsla(0.0, 0.0, 0.0, 0.06),
                row_alt: hsla(0.0, 0.0, 0.0, 0.025),
                text_primary: rgb(0x1A1A1A).into(),
                text_secondary: hsla(0.0, 0.0, 0.0, 0.70),
                text_muted: hsla(0.0, 0.0, 0.0, 0.45),
                accent: rgb(0x5B5FC7).into(),
                success: rgb(0x15803D).into(),
                warning: rgb(0xB45309).into(),
                danger: rgb(0xDC2626).into(),
            },
        }
    }
}

/// Apply GPUI Component's theme mode and align key semantic surfaces with Larik's calm tokens.
pub fn apply_larik_theme(mode: ThemePreference, window: Option<&mut Window>, cx: &mut App) {
    let gpui_mode = match mode {
        ThemePreference::Dark => ThemeMode::Dark,
        ThemePreference::Light => ThemeMode::Light,
    };
    Theme::change(gpui_mode, window, cx);

    let p = StudioPalette::for_mode(mode);
    let theme = Theme::global_mut(cx);
    theme.colors.background = p.background;
    theme.colors.secondary = p.elevated;
    theme.colors.sidebar = p.rail_bg;
    theme.colors.border = p.hairline;
    theme.colors.muted = p.elevated;
    theme.colors.primary = p.accent;
    theme.colors.table = p.background;
    theme.colors.table_even = p.row_alt;
    theme.colors.table_head = p.rail_bg;
    theme.colors.table_row_border = p.hairline_subtle;
    theme.font_family = "Segoe UI".into();
    theme.mono_font_family = "Cascadia Mono".into();
    theme.mono_font_size = gpui_kit::px(13.5);
    cx.refresh_windows();
}

/// Parse a `#RRGGBB` space identity hex string into an `Hsla` color.
pub fn parse_hex_color(hex: &str) -> Hsla {
    let trimmed = hex.trim().trim_start_matches('#');
    if let Ok(value) = u32::from_str_radix(trimmed, 16) {
        rgb(value).into()
    } else {
        rgb(0x6366F1).into()
    }
}
