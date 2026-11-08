//! Ported from `InterfaceScale.cs`. Owned by package `w6-ui-base`.
//!
//! How large the interface is drawn, as a zoom on every window.
//!
//! A zoom rather than a font size: the windows carry some two hundred fixed text sizes and as many
//! fixed widths, so larger text alone would overflow the buttons and rows it sits in. Scaling the
//! whole of each window keeps every proportion, and egui redraws text and the self-painted views
//! at the new size rather than stretching a bitmap, so it stays sharp.
//!
//! In egui this is `Context::set_zoom_factor`, which multiplies the pixel density for every
//! viewport at once. That covers what the C# needed a `UiScale` resource and a layout transform on
//! each window for, and its popups, menus, drop-downs and tooltips: they are all drawn by the same
//! context. Fonts are deliberately not scaled as well.
//!
//! Shortcuts: Ctrl + / Ctrl = (larger), Ctrl - (smaller) and Ctrl 0 (actual size), with Cmd for
//! Ctrl on macOS. egui has its own keyboard zoom in 10% steps, which would fight this one, so
//! [`handle_shortcuts`] switches it off.

use egui::{Context, Key, KeyboardShortcut, Modifiers};
use olv_core::Result;
use olv_store::store::settings_store::SettingsStore;

/// The sizes offered, as fractions of the design size.
pub const STEPS: [f64; 6] = [1.0, 1.15, 1.25, 1.5, 1.75, 2.0];

/// The offered step nearest a value, so a stored or typed scale lands on one. The first of two
/// equally near steps wins; anything that is not a number is actual size.
pub fn snap(scale: f64) -> f64 {
    if !scale.is_finite() {
        return 1.0;
    }

    let mut best = STEPS[0];
    for step in STEPS {
        if (step - scale).abs() < (best - scale).abs() {
            best = step;
        }
    }
    best
}

/// The first step above the (snapped) scale, or the largest when already there.
pub fn larger(scale: f64) -> f64 {
    let snapped = snap(scale);
    STEPS
        .iter()
        .copied()
        .find(|&s| s > snapped + 1e-9)
        .unwrap_or(STEPS[STEPS.len() - 1])
}

/// The last step below the (snapped) scale, or actual size when already there.
pub fn smaller(scale: f64) -> f64 {
    let snapped = snap(scale);
    STEPS.iter().copied().rfind(|&s| s < snapped - 1e-9).unwrap_or(STEPS[0])
}

/// `150%`.
pub fn label(scale: f64) -> String {
    // .NET's "0" format rounds half away from zero, which `f64::round` also does.
    format!("{}%", (scale * 100.0).round() as i64)
}

fn key() -> egui::Id {
    egui::Id::unique("olv-interface-scale")
}

pub struct InterfaceScale;

impl InterfaceScale {
    /// The zoom the context is showing, or is about to: egui takes a new zoom factor at the end of
    /// the pass, so the one asked for is remembered here to answer for the rest of that pass.
    pub fn current(ctx: &Context) -> f64 {
        ctx.data(|d| d.get_temp::<f64>(key()))
            .unwrap_or_else(|| f64::from(ctx.zoom_factor()))
    }

    /// Zooms every window to the step nearest `scale`, and returns the step.
    pub fn apply(ctx: &Context, scale: f64) -> f64 {
        let snapped = snap(scale);
        ctx.data_mut(|d| d.insert_temp(key(), snapped));
        ctx.set_zoom_factor(snapped as f32);
        ctx.request_repaint();
        snapped
    }

    /// The scale stored in the settings, snapped to a step (the store itself honours anything in
    /// 1 to 3, and a file without one reads as actual size).
    pub fn stored(settings: &SettingsStore) -> f64 {
        snap(settings.ui_scale())
    }

    /// The user chose a size: shows it and stores it. A value between steps is stored as the
    /// nearest step.
    pub fn choose(ctx: &Context, settings: &SettingsStore, scale: f64) -> Result<f64> {
        let snapped = Self::apply(ctx, scale);
        settings.set_ui_scale(snapped)?;
        Ok(snapped)
    }

    /// `--zoom <x>`: shows it for this run only and leaves the stored preference alone.
    pub fn preview(ctx: &Context, scale: f64) -> f64 {
        Self::apply(ctx, scale)
    }

    /// Reads the zoom shortcuts for this frame. Returns the size the user asked for, if any, for
    /// the caller to [`InterfaceScale::choose`]. Call once per frame, before drawing.
    pub fn handle_shortcuts(ctx: &Context) -> Option<f64> {
        // egui's own Ctrl +/- zoom moves in 10% steps, outside our list, and persists nothing.
        ctx.options_mut(|o| o.zoom_with_keyboard = false);

        let current = Self::current(ctx);
        ctx.input_mut(|input| {
            // The unshifted = key and the shifted + key both mean "larger", on any layout that
            // has one of them. (egui ignores an extra Shift when matching.)
            let larger_keys = [
                KeyboardShortcut::new(Modifiers::COMMAND, Key::Equals),
                KeyboardShortcut::new(Modifiers::COMMAND, Key::Plus),
            ];
            let smaller_keys = [KeyboardShortcut::new(Modifiers::COMMAND, Key::Minus)];
            let reset_keys = [KeyboardShortcut::new(Modifiers::COMMAND, Key::Num0)];

            // Consume all of each group so a held key is not read twice.
            let larger_pressed = larger_keys.iter().fold(false, |any, s| input.consume_shortcut(s) | any);
            let smaller_pressed = smaller_keys
                .iter()
                .fold(false, |any, s| input.consume_shortcut(s) | any);
            let reset_pressed = reset_keys.iter().fold(false, |any, s| input.consume_shortcut(s) | any);

            if reset_pressed {
                Some(1.0)
            } else if larger_pressed {
                Some(self::larger(current))
            } else if smaller_pressed {
                Some(self::smaller(current))
            } else {
                None
            }
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    fn settings_in(name: &str) -> (PathBuf, SettingsStore) {
        let dir = std::env::temp_dir().join(format!("olv-scale-{}-{name}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("settings.json");
        let _ = std::fs::remove_file(&path);
        let store = SettingsStore::new(path.clone());
        (path, store)
    }

    #[test]
    fn any_value_lands_on_an_offered_step() {
        assert_eq!(1.0, snap(0.2));
        assert_eq!(1.5, snap(1.45));
        assert_eq!(2.0, snap(9.0));
        assert_eq!(1.0, snap(f64::NAN));
        assert_eq!(1.0, snap(f64::INFINITY));
        assert_eq!(1.0, snap(f64::NEG_INFINITY));
    }

    #[test]
    fn equal_distances_pick_the_first_step() {
        // 1.075 is exactly between 1.0 and 1.15.
        assert_eq!(1.0, snap(1.075));
    }

    #[test]
    fn zooming_steps_through_the_sizes_and_stops_at_the_ends() {
        assert_eq!(1.15, larger(1.0));
        assert_eq!(2.0, larger(2.0));
        assert_eq!(1.25, smaller(1.5));
        assert_eq!(1.0, smaller(1.0));

        // From an in-between value it moves from the step it would have snapped to.
        assert_eq!(1.75, larger(1.5));
        assert_eq!(1.15, smaller(1.25));
        assert_eq!(1.15, larger(f64::NAN));
    }

    #[test]
    fn the_labels_are_whole_percentages() {
        let labels: Vec<String> = STEPS.iter().map(|&s| label(s)).collect();
        assert_eq!(["100%", "115%", "125%", "150%", "175%", "200%"], labels.as_slice());
    }

    #[test]
    fn applying_a_size_zooms_the_whole_context_to_the_nearest_step() {
        let ctx = Context::default();
        assert_eq!(1.5, InterfaceScale::apply(&ctx, 1.6));
        assert_eq!(1.5, InterfaceScale::current(&ctx));
    }

    #[test]
    fn choosing_a_size_records_it_and_it_comes_back_next_time() {
        let (path, store) = settings_in("choose");
        let ctx = Context::default();

        InterfaceScale::choose(&ctx, &store, 1.5).unwrap();

        assert_eq!(1.5, SettingsStore::new(path).ui_scale());
        assert_eq!(1.5, InterfaceScale::stored(&store));
    }

    #[test]
    fn a_size_between_steps_is_stored_as_the_nearest_step() {
        let (path, store) = settings_in("between");
        let ctx = Context::default();

        InterfaceScale::choose(&ctx, &store, 1.6).unwrap();

        assert_eq!(1.5, SettingsStore::new(path).ui_scale());
    }

    #[test]
    fn previewing_a_size_leaves_the_stored_preference_alone() {
        let (path, store) = settings_in("preview");
        let ctx = Context::default();

        InterfaceScale::choose(&ctx, &store, 1.25).unwrap();
        InterfaceScale::preview(&ctx, 2.0);

        assert_eq!(2.0, InterfaceScale::current(&ctx));
        assert_eq!(1.25, SettingsStore::new(path).ui_scale());
    }

    #[test]
    fn a_stored_size_that_would_make_the_window_unusable_is_ignored() {
        // A hand-edited file must not leave the window too small or too large to reach the menu
        // that puts it back.
        for stored in ["0", "40", "-1"] {
            let (path, _) = settings_in(&format!("bad{stored}"));
            std::fs::write(&path, format!(r#"{{ "Version": 1, "UiScale": {stored} }}"#)).unwrap();
            assert_eq!(
                1.0,
                InterfaceScale::stored(&SettingsStore::new(path)),
                "stored {stored}"
            );
        }
    }

    #[test]
    fn a_settings_file_from_before_zoom_existed_starts_at_actual_size() {
        let (path, _) = settings_in("old");
        std::fs::write(&path, r#"{ "Version": 1, "ThemeId": "nord" }"#).unwrap();
        assert_eq!(1.0, InterfaceScale::stored(&SettingsStore::new(path)));
    }

    #[test]
    fn the_keyboard_asks_for_the_next_size_the_previous_one_or_actual_size() {
        let ctx = Context::default();
        InterfaceScale::apply(&ctx, 1.5);

        let press = |key: Key, modifiers: Modifiers| {
            let mut raw = egui::RawInput::default();
            raw.events.push(egui::Event::Key {
                key,
                physical_key: None,
                pressed: true,
                repeat: false,
                modifiers,
            });
            let mut asked = None;
            ctx.run_ui(raw, |ui| asked = InterfaceScale::handle_shortcuts(ui.ctx()))
                .textures_delta
                .clear();
            asked
        };

        assert_eq!(Some(1.75), press(Key::Equals, Modifiers::COMMAND));
        assert_eq!(Some(1.75), press(Key::Plus, Modifiers::COMMAND | Modifiers::SHIFT));
        assert_eq!(Some(1.25), press(Key::Minus, Modifiers::COMMAND));
        assert_eq!(Some(1.0), press(Key::Num0, Modifiers::COMMAND));
        assert_eq!(None, press(Key::Equals, Modifiers::NONE), "a bare = is just typing");
        assert!(
            !ctx.options(|o| o.zoom_with_keyboard),
            "egui's own 10% zoom must be off"
        );
    }
}
