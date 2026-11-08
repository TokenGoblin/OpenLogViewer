//! Ported from `ThemeManager.cs`. Owned by package `w6-ui-base`.
//!
//! Holds the active theme and publishes it two ways: into egui's style and fonts (see
//! [`super::egui_theme`]), which every widget picks up, and as [`ThemeManager::current`], which
//! the surfaces that paint themselves read each frame.
//!
//! What the C# had and egui does not need:
//!
//! * `Changed` event. Nothing subscribes in egui: every view is redrawn each frame from the
//!   current theme, and [`ThemeManager::apply`] asks for a repaint. A view that caches something
//!   derived from the theme (a rendered ramp, a glyph layout) keys it on
//!   [`ThemeManager::generation`].
//! * `FollowAllWindows` / `Follow`. egui styles the whole context, so every window the
//!   application opens, now or later, in any viewport, already follows the theme and the zoom;
//!   there is no window that could open in the default font.
//! * Per-window title bar subscription. [`super::title_bar::apply`] is called by the shell when
//!   the generation changes (see `olv-themes` for the pattern).
//!
//! The state lives in the egui `Context` rather than in a static, so two contexts (and the tests
//! that run side by side) never share a theme.

use egui::Context;
use olv_core::Result;
use olv_store::store::settings_store::SettingsStore;

use super::egui_theme;
use super::theme::Theme;
use super::theme_catalog::ThemeCatalog;

#[derive(Clone, Copy)]
struct Current {
    theme: &'static Theme,
    generation: u64,
}

fn key() -> egui::Id {
    egui::Id::unique("olv-theme-manager")
}

pub struct ThemeManager;

impl ThemeManager {
    /// The active theme: the default until one is applied.
    pub fn current(ctx: &Context) -> &'static Theme {
        Self::state(ctx).theme
    }

    /// Counts the times a theme has been applied, so a view can tell when what it derived from
    /// the old one is stale.
    pub fn generation(ctx: &Context) -> u64 {
        Self::state(ctx).generation
    }

    fn state(ctx: &Context) -> Current {
        ctx.data(|d| d.get_temp::<Current>(key())).unwrap_or(Current {
            theme: ThemeCatalog::default_theme(),
            generation: 0,
        })
    }

    /// Makes `theme` the active one and pushes it into egui, replacing every token at once, then
    /// asks for a repaint. Applying the theme already active still repaints.
    pub fn apply(ctx: &Context, theme: &'static Theme) {
        let generation = Self::state(ctx).generation + 1;
        ctx.data_mut(|d| d.insert_temp(key(), Current { theme, generation }));
        egui_theme::apply(ctx, theme);
        ctx.request_repaint();
    }

    /// Applies the named theme, or the default when the id is unknown or missing.
    pub fn apply_id(ctx: &Context, id: Option<&str>) -> &'static Theme {
        let theme = ThemeCatalog::find(id);
        Self::apply(ctx, theme);
        theme
    }

    /// Call at the start of each frame. Cheap unless the pixel density changed (the zoom, or the
    /// window moved to another display), in which case pixel text is resized to suit.
    pub fn begin_frame(ctx: &Context) {
        // The first frame applies the default theme, so no widget is ever drawn unstyled.
        if ctx.data(|d| d.get_temp::<Current>(key())).is_none() {
            Self::apply(ctx, ThemeCatalog::default_theme());
        } else {
            egui_theme::apply(ctx, Self::state(ctx).theme);
        }
    }

    /// The user picked `theme` from the menu: applies it and stores its id. Picking the theme
    /// already active changes nothing and stores nothing, and the result says whether anything
    /// happened.
    pub fn choose(ctx: &Context, settings: &SettingsStore, theme: &'static Theme) -> Result<bool> {
        if Self::state(ctx).theme.id == theme.id && Self::generation(ctx) > 0 {
            return Ok(false);
        }

        Self::apply(ctx, theme);
        settings.set_theme(Some(theme.id))?;
        Ok(true)
    }

    /// `--theme <id>`: applies it for this run only and leaves the stored preference alone. An
    /// unknown id falls back to the default rather than failing.
    pub fn preview(ctx: &Context, id: &str) -> &'static Theme {
        Self::apply_id(ctx, Some(id))
    }

    /// The theme stored in the settings (the default when none, or an unknown id), applied before
    /// the first frame is drawn so none is drawn in another theme.
    pub fn apply_stored(ctx: &Context, settings: &SettingsStore) -> &'static Theme {
        Self::apply_id(ctx, settings.theme_id().as_deref())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use olv_store::store::settings_store::SettingsStore;

    fn store() -> (tempfile_dir::Dir, SettingsStore) {
        let dir = tempfile_dir::Dir::new();
        let store = SettingsStore::new(dir.path().join("settings.json"));
        (dir, store)
    }

    /// A temp directory without a dependency for it.
    mod tempfile_dir {
        use std::path::{Path, PathBuf};

        pub struct Dir(PathBuf);

        impl Dir {
            pub fn new() -> Self {
                static N: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);
                let n = N.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                let path = std::env::temp_dir().join(format!("olv-theme-{}-{n}", std::process::id()));
                std::fs::create_dir_all(&path).unwrap();
                Self(path)
            }

            pub fn path(&self) -> &Path {
                &self.0
            }
        }

        impl Drop for Dir {
            fn drop(&mut self) {
                let _ = std::fs::remove_dir_all(&self.0);
            }
        }
    }

    #[test]
    fn a_new_context_starts_on_the_default_theme() {
        let ctx = Context::default();
        assert_eq!(ThemeCatalog::default_theme().id, ThemeManager::current(&ctx).id);
    }

    #[test]
    fn choosing_a_theme_records_it_and_it_comes_back_next_time() {
        let (dir, settings) = store();
        let ctx = Context::default();

        ThemeManager::choose(&ctx, &settings, ThemeCatalog::find(Some("gruvbox"))).unwrap();
        assert_eq!("gruvbox", ThemeManager::current(&ctx).id);

        let reloaded = SettingsStore::new(dir.path().join("settings.json"));
        assert_eq!(Some("gruvbox".to_owned()), reloaded.theme_id());

        let next_run = Context::default();
        assert_eq!("gruvbox", ThemeManager::apply_stored(&next_run, &reloaded).id);
        assert_eq!("gruvbox", ThemeManager::current(&next_run).id);
    }

    #[test]
    fn previewing_a_theme_leaves_the_stored_preference_alone() {
        // The --theme switch is for one-off runs such as a screenshot; it must not overwrite what
        // the user chose.
        let (dir, settings) = store();
        let ctx = Context::default();

        ThemeManager::choose(&ctx, &settings, ThemeCatalog::find(Some("paper"))).unwrap();
        ThemeManager::preview(&ctx, "monokai");

        assert_eq!("monokai", ThemeManager::current(&ctx).id);
        let reloaded = SettingsStore::new(dir.path().join("settings.json"));
        assert_eq!(Some("paper".to_owned()), reloaded.theme_id());
    }

    #[test]
    fn previewing_an_unknown_theme_falls_back_rather_than_failing() {
        let ctx = Context::default();
        ThemeManager::preview(&ctx, "not-a-theme");
        assert_eq!(ThemeCatalog::default_theme().id, ThemeManager::current(&ctx).id);
    }

    #[test]
    fn reselecting_the_current_theme_is_a_no_op() {
        let (_dir, settings) = store();
        let ctx = Context::default();

        assert!(ThemeManager::choose(&ctx, &settings, ThemeCatalog::find(Some("one-dark"))).unwrap());
        let generation = ThemeManager::generation(&ctx);

        assert!(!ThemeManager::choose(&ctx, &settings, ThemeCatalog::find(Some("one-dark"))).unwrap());
        assert_eq!(
            generation,
            ThemeManager::generation(&ctx),
            "views must not be told to redo their work"
        );
    }

    #[test]
    fn switching_theme_replaces_the_style_at_once() {
        // The egui end of "switching theme repaints the traces": views read the current theme each
        // frame, so what must hold is that the context never holds half of each.
        let ctx = Context::default();
        ThemeManager::apply_id(&ctx, Some("daylight"));
        let before = ThemeManager::generation(&ctx);

        ThemeManager::apply_id(&ctx, Some("nord"));

        assert!(ThemeManager::generation(&ctx) > before);
        assert_eq!("nord", ThemeManager::current(&ctx).id);
        assert_eq!(
            ThemeManager::current(&ctx).panel.to_color32(),
            ctx.global_style().visuals.panel_fill
        );
        assert!(ctx.global_style().visuals.dark_mode);
    }

    #[test]
    fn the_first_frame_is_never_drawn_unstyled() {
        let ctx = Context::default();
        ThemeManager::begin_frame(&ctx);
        assert_eq!(
            ThemeCatalog::default_theme().panel.to_color32(),
            ctx.global_style().visuals.panel_fill
        );
    }

    #[test]
    fn two_contexts_do_not_share_a_theme() {
        let a = Context::default();
        let b = Context::default();
        ThemeManager::apply_id(&a, Some("retro"));
        assert_eq!("retro", ThemeManager::current(&a).id);
        assert_eq!(ThemeCatalog::default_theme().id, ThemeManager::current(&b).id);
    }
}
