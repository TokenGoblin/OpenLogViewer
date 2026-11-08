//! Not in the C#: the egui end of `ThemeManager.Publish`.
//!
//! Turns a [`Theme`] into egui's `Visuals`, `Style`, text styles and font definitions. The C#
//! wrote brushes, fonts and corner radii into the application's resource dictionary and let the
//! XAML bind to them; egui has no such dictionary, so the equivalent is the context's style.
//! Anything a widget reads from the style follows the theme with no further work; anything a
//! screen draws for itself reads the theme (see [`ThemeManager::current`]) each frame.
//!
//! Every token is replaced on every apply, so nothing from the previous theme can survive.

use egui::style::{Selection, WidgetVisuals};
use egui::{Color32, Context, CornerRadius, FontFamily, Margin, Shadow, Stroke, Style, TextStyle, Visuals, vec2};

use super::theme::{Corner, Theme};
use super::theme_fonts::{FontSet, ThemeFonts};

/// What was last pushed into a context, so applying again with nothing changed costs nothing.
#[derive(Clone, PartialEq)]
struct Applied {
    theme: &'static str,
    pixels_per_point_bits: u32,
    fonts: FontSet,
}

fn key() -> egui::Id {
    egui::Id::unique("olv-egui-theme-applied")
}

/// Pushes `theme` into `ctx`: fonts (only when the font set changed, as rebuilding the atlas is
/// the one expensive part), the style and visuals, and the dark/light flag. Applies again when the
/// pixel density changes, because pixel text is sized from it.
///
/// Returns true when anything was pushed.
pub fn apply(ctx: &Context, theme: &'static Theme) -> bool {
    let fonts = FontSet::of(theme);
    let applied = Applied {
        theme: theme.id,
        pixels_per_point_bits: ctx.pixels_per_point().to_bits(),
        fonts,
    };

    let previous: Option<Applied> = ctx.data(|d| d.get_temp(key()));
    if previous.as_ref() == Some(&applied) {
        return false;
    }

    if previous.as_ref().is_none_or(|p| p.fonts != fonts) {
        ctx.set_fonts(ThemeFonts::definitions(fonts));
    }

    ctx.set_theme(if theme.is_dark() {
        egui::Theme::Dark
    } else {
        egui::Theme::Light
    });
    let style = style(ctx, theme);
    ctx.all_styles_mut(|s| *s = style.clone());

    ctx.data_mut(|d| d.insert_temp(key(), applied));
    ctx.request_repaint();
    true
}

/// A corner radius for egui, from the theme's style: 0 for a theme that squares the application
/// off. Whole points, as egui's are.
pub fn corner(theme: &Theme, corner: Corner) -> CornerRadius {
    CornerRadius::same(theme.radius(corner).round().clamp(0.0, 255.0) as u8)
}

/// The whole style: visuals, spacing and the five text styles.
pub fn style(ctx: &Context, theme: &Theme) -> Style {
    let mut style = Style {
        visuals: visuals(theme),
        ..Style::default()
    };

    // Design sizes before zoom: the C# draws body text at 12-13 px, captions at 11, titles at 18+.
    let ui = |size| ThemeFonts::ui(ctx, theme, size);
    style.text_styles = [
        (TextStyle::Small, ui(11.0)),
        (TextStyle::Body, ui(13.0)),
        (TextStyle::Button, ui(13.0)),
        (TextStyle::Heading, ui(18.0)),
        (TextStyle::Monospace, ThemeFonts::data(ctx, theme, 13.0)),
    ]
    .into();

    // UI-SPEC "Shared widget styles": buttons pad 12x5, menus 4, the scrollbar is 11 wide, always
    // drawn (never floating), and has no arrow buttons.
    let spacing = &mut style.spacing;
    spacing.button_padding = vec2(12.0, 5.0);
    spacing.menu_margin = Margin::same(4);
    spacing.scroll.floating = false;
    // The thumb takes the widget *background* fill (see `scroll_area`), not the ink.
    spacing.scroll.foreground_color = false;
    spacing.scroll.bar_width = 11.0;
    spacing.scroll.bar_inner_margin = 2.0;
    spacing.scroll.bar_outer_margin = 0.0;

    // The caret and selection are drawn in the ink, as the C# text box does.
    style.visuals.text_cursor.stroke = Stroke::new(2.0, theme.text.to_color32());
    style
}

/// The colours, strokes and corner radii of every widget state.
pub fn visuals(theme: &Theme) -> Visuals {
    let mut v = if theme.is_dark() {
        Visuals::dark()
    } else {
        Visuals::light()
    };
    let c = |color: super::color_math::Color| color.to_color32();
    let line = Stroke::new(1.0, c(theme.line));

    // Text: the ink everywhere, `Muted` for weak text (captions, hints).
    v.override_text_color = None;
    v.weak_text_color = Some(c(theme.muted));
    v.hyperlink_color = c(theme.accent);
    v.warn_fg_color = c(theme.warning());
    v.error_fg_color = c(theme.danger());

    // Grounds.
    v.panel_fill = c(theme.panel);
    v.window_fill = c(theme.panel_alt);
    v.window_stroke = line;
    v.extreme_bg_color = c(theme.background);
    v.text_edit_bg_color = Some(c(theme.background));
    v.faint_bg_color = c(theme.header());
    v.code_bg_color = c(theme.card());
    v.selection = Selection {
        bg_fill: c(theme.selected()),
        stroke: Stroke::new(1.0, c(theme.accent)),
    };

    // Corners, and the shadows a square, flat theme does not have.
    v.window_corner_radius = corner(theme, Corner::Large);
    v.menu_corner_radius = corner(theme, Corner::Large);
    if theme.style.corner_scale == 0.0 {
        v.window_shadow = Shadow::NONE;
        v.popup_shadow = Shadow::NONE;
    }

    let state = |bg: Color32, stroke: Stroke, ink: Color32| WidgetVisuals {
        bg_fill: bg,
        weak_bg_fill: bg,
        bg_stroke: stroke,
        corner_radius: corner(theme, Corner::Normal),
        fg_stroke: Stroke::new(1.0, ink),
        expansion: 0.0,
    };

    // Labels and frames that do nothing when poked.
    v.widgets.noninteractive = state(c(theme.panel), line, c(theme.text));
    // Buttons at rest: PanelAlt ground, 1 px Line border.
    v.widgets.inactive = state(c(theme.panel_alt), line, c(theme.text));
    // Hovered: only the ground changes.
    v.widgets.hovered = state(c(theme.hover()), line, c(theme.text));
    // Pressed or checked: the Selected ground with the Accent border and ink.
    v.widgets.active = state(c(theme.selected()), Stroke::new(1.0, c(theme.accent)), c(theme.accent));
    // An open menu or combo box keeps its ground and shows the Accent border.
    v.widgets.open = state(c(theme.panel_alt), Stroke::new(1.0, c(theme.accent)), c(theme.text));

    // Pixel text: no smoothing as far as egui allows (see `theme_fonts`).
    if theme.style.pixel_text {
        v.text_options.font_hinting = true;
        v.text_options.subpixel_binning = false;
        v.text_options.color_transfer_function = egui::epaint::FontColorTransferFunction::Off;
    }

    v
}

/// A scroll area whose bar is drawn the way UI-SPEC "Scrollbar" says: the thumb in `Scroll`,
/// `ScrollHover` under the pointer and `Accent` while dragged, rounded by `CornerSmall`.
///
/// Every screen's scroll areas go through this rather than `ScrollArea::show` directly. egui
/// paints the thumb with the *background fill* of the inactive, hovered and active widget states,
/// which are also what checkboxes and slider rails are filled with; a global style that made the
/// thumb right would make those wrong. So the bar's colours are set for the scroll area alone and
/// put back for what is inside it.
///
/// Limits: egui draws the track in the panel's extreme-background colour rather than leaving it
/// transparent, and has no click-to-page behaviour on it.
pub fn scroll_area<R>(
    ui: &mut egui::Ui,
    theme: &Theme,
    area: egui::ScrollArea,
    add_contents: impl FnOnce(&mut egui::Ui) -> R,
) -> egui::scroll_area::ScrollAreaOutput<R> {
    let inside = ui.visuals().widgets.clone();
    ui.scope(|ui| {
        let small = corner(theme, Corner::Small);
        let widgets = &mut ui.visuals_mut().widgets;
        widgets.inactive.bg_fill = theme.scroll().to_color32();
        widgets.hovered.bg_fill = theme.scroll_hover().to_color32();
        widgets.active.bg_fill = theme.accent.to_color32();
        for state in [&mut widgets.inactive, &mut widgets.hovered, &mut widgets.active] {
            state.corner_radius = small;
            state.expansion = 0.0;
        }

        area.show(ui, |ui| {
            ui.visuals_mut().widgets = inside;
            add_contents(ui)
        })
    })
    .inner
}

/// The font family a text style resolves to, for tests and for views that pick a family by role.
pub fn family_of(style: &Style, text_style: &TextStyle) -> FontFamily {
    style.text_styles[text_style].family.clone()
}

#[cfg(test)]
mod tests {
    use super::super::theme_catalog::ThemeCatalog;
    use super::*;

    #[test]
    fn every_theme_becomes_visuals_in_its_own_colours() {
        for theme in ThemeCatalog::themes() {
            let v = visuals(theme);
            assert_eq!(theme.is_dark(), v.dark_mode, "{}", theme.id);
            assert_eq!(theme.panel.to_color32(), v.panel_fill, "{}", theme.id);
            assert_eq!(theme.background.to_color32(), v.extreme_bg_color, "{}", theme.id);
            assert_eq!(
                theme.panel_alt.to_color32(),
                v.widgets.inactive.weak_bg_fill,
                "{}",
                theme.id
            );
            assert_eq!(
                theme.hover().to_color32(),
                v.widgets.hovered.weak_bg_fill,
                "{}",
                theme.id
            );
            assert_eq!(
                theme.text.to_color32(),
                v.widgets.inactive.fg_stroke.color,
                "{}",
                theme.id
            );
            assert_eq!(
                theme.accent.to_color32(),
                v.widgets.active.bg_stroke.color,
                "{}",
                theme.id
            );
            assert_eq!(theme.selected().to_color32(), v.selection.bg_fill, "{}", theme.id);
        }
    }

    #[test]
    fn a_square_theme_has_no_rounded_corners_anywhere() {
        let retro = visuals(ThemeCatalog::find(Some("retro")));
        let square = CornerRadius::ZERO;

        assert_eq!(square, retro.window_corner_radius);
        assert_eq!(square, retro.menu_corner_radius);
        for w in [
            &retro.widgets.noninteractive,
            &retro.widgets.inactive,
            &retro.widgets.hovered,
            &retro.widgets.active,
            &retro.widgets.open,
        ] {
            assert_eq!(square, w.corner_radius);
        }

        let midnight = visuals(ThemeCatalog::find(Some("midnight")));
        assert_eq!(CornerRadius::same(4), midnight.widgets.inactive.corner_radius);
        assert_eq!(CornerRadius::same(6), midnight.window_corner_radius);
    }

    #[test]
    fn retro_asks_egui_for_pixel_text_and_the_others_do_not() {
        let retro = visuals(ThemeCatalog::find(Some("retro")));
        assert!(!retro.text_options.subpixel_binning);
        assert!(retro.text_options.font_hinting);

        let midnight = visuals(ThemeCatalog::find(Some("midnight")));
        assert!(midnight.text_options.subpixel_binning);
    }

    #[test]
    fn applying_a_theme_replaces_everything_and_applying_it_again_does_nothing() {
        let ctx = Context::default();
        let midnight = ThemeCatalog::find(Some("midnight"));
        let daylight = ThemeCatalog::find(Some("daylight"));
        let retro = ThemeCatalog::find(Some("retro"));

        assert!(apply(&ctx, midnight));
        assert!(!apply(&ctx, midnight), "nothing changed, so nothing should be pushed");

        assert!(apply(&ctx, daylight));
        let style = ctx.global_style();
        assert!(!style.visuals.dark_mode);
        assert_eq!(daylight.panel.to_color32(), style.visuals.panel_fill);

        // Nothing of the dark theme survives, and nothing of the rounded ones.
        assert!(apply(&ctx, retro));
        let style = ctx.global_style();
        assert!(style.visuals.dark_mode);
        assert_eq!(retro.panel.to_color32(), style.visuals.panel_fill);
        assert_eq!(CornerRadius::ZERO, style.visuals.widgets.inactive.corner_radius);
    }

    #[test]
    fn pixel_text_is_resized_when_the_zoom_changes() {
        let ctx = Context::default();
        let retro = ThemeCatalog::find(Some("retro"));

        assert!(apply(&ctx, retro));
        let at_one = ctx.global_style().text_styles[&TextStyle::Body].size;
        assert_eq!(16.0, at_one);

        ctx.set_zoom_factor(2.0);
        // The zoom becomes the pixel density at the start of the next pass.
        ctx.run_ui(Default::default(), |_| {}).textures_delta.clear();
        assert!(apply(&ctx, retro), "a new pixel density needs new sizes");
        let at_two = ctx.global_style().text_styles[&TextStyle::Body].size;
        assert_eq!(16.0, at_two, "32 physical pixels at 2x is 16 points");
    }

    #[test]
    fn retro_text_goes_through_the_pixel_font_family() {
        let ctx = Context::default();
        apply(&ctx, ThemeCatalog::find(Some("retro")));
        let style = ctx.global_style();
        assert_eq!(FontFamily::Proportional, family_of(&style, &TextStyle::Body));
        assert_eq!(FontFamily::Monospace, family_of(&style, &TextStyle::Monospace));
    }
}
