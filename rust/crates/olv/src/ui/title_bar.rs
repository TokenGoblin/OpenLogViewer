//! Ported from `TitleBar.cs`. Owned by package `w6-ui-base`.
//!
//! Colours the window's title bar to match the theme.
//!
//! The toolkit leaves the title bar to the operating system, so it stays in the system's colours
//! while everything below it follows the chosen scheme: a white strip above a dark application.
//! Windows 11 lets an app set the caption, its text and the border directly, which is the only way
//! to make the two agree.
//!
//! # Per operating system
//!
//! * **Windows**: through the `windows` crate's `DwmSetWindowAttribute`: the dark-title-bar flag
//!   from the theme's `IsDark`, and on Windows 11 the caption colour (`Chrome`), caption text
//!   (`Text`) and border (`Line`). Every call is best-effort. The caption colours arrived in
//!   Windows 11 and the dark-mode flag changed number during Windows 10; on anything older these
//!   fail and leave the title bar as it was, which is what a system title bar looked like before
//!   anyone asked.
//! * **Linux and macOS**: nothing is coloured. The window manager owns the decorations and exposes
//!   no colour to an application (UI-SPEC §1 leaves a custom-drawn title bar as an open question
//!   for the user; OPEN-QUESTION: 4.5). [`apply`] therefore returns false there, and [`viewport_command`] still gives
//!   the dark/light *hint* egui can pass to the windowing system, which GNOME, KDE and macOS
//!   honour.
//!
//! The one `unsafe` in the interface layer is the FFI call below, which is why this module opts out
//! of the workspace's `unsafe_code = "deny"` (CONVENTIONS.md allows it for operating-system calls
//! through Rust bindings; this is the only such call outside `olv-transport`).

#![allow(unsafe_code)]

use egui::{SystemTheme, ViewportCommand};
use raw_window_handle::HasWindowHandle;

use super::color_math::Color;
use super::theme::Theme;

/// Dark title bar. 20 on current builds, 19 on early Windows 10.
pub const IMMERSIVE_DARK_MODE: u32 = 20;
pub const IMMERSIVE_DARK_MODE_LEGACY: u32 = 19;

/// Windows 11 only: exact colours, rather than merely light or dark.
pub const BORDER_COLOUR: u32 = 34;
pub const CAPTION_COLOUR: u32 = 35;
pub const TEXT_COLOUR: u32 = 36;

/// A Win32 `COLORREF`, which is `0x00BBGGRR` rather than the usual order.
pub fn color_ref(colour: Color) -> u32 {
    u32::from(colour.r) | (u32::from(colour.g) << 8) | (u32::from(colour.b) << 16)
}

/// What [`apply`] sets, worked out from the theme alone, so it can be checked on every platform.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub struct Plan {
    /// The dark title bar flag (1 or 0).
    pub dark: u32,
    pub caption: u32,
    pub text: u32,
    pub border: u32,
}

impl Plan {
    pub fn of(theme: &Theme) -> Self {
        Self {
            dark: u32::from(theme.is_dark()),
            // The chrome rather than the plot ground: the title bar sits directly above the menu
            // bar, and matching its neighbour is what stops the seam showing. Chrome is the panel
            // colour unless a theme frames the window in something else.
            caption: color_ref(theme.chrome()),
            text: color_ref(theme.text),
            border: color_ref(theme.line),
        }
    }
}

/// The dark/light hint for the windowing system, for the platforms where a colour cannot be set.
pub fn viewport_command(theme: &Theme) -> ViewportCommand {
    ViewportCommand::SetTheme(if theme.is_dark() {
        SystemTheme::Dark
    } else {
        SystemTheme::Light
    })
}

/// Colours the title bar of `window` (on Windows), and tells the windowing system whether the
/// theme is dark or light (everywhere). Safe to call every time the theme changes, and once when
/// the window is first shown; before the window has a native handle it does nothing.
///
/// Returns true when a title bar colour was set, which is only ever on Windows.
pub fn apply(ctx: &egui::Context, window: &impl HasWindowHandle, theme: &Theme) -> bool {
    ctx.send_viewport_cmd(viewport_command(theme));

    let Ok(handle) = window.window_handle() else {
        return false;
    };
    platform::apply(handle.as_raw(), &Plan::of(theme))
}

#[cfg(windows)]
mod platform {
    use raw_window_handle::RawWindowHandle;
    use windows::Win32::Foundation::HWND;
    use windows::Win32::Graphics::Dwm::{DWMWINDOWATTRIBUTE, DwmSetWindowAttribute};

    use super::{BORDER_COLOUR, CAPTION_COLOUR, IMMERSIVE_DARK_MODE, IMMERSIVE_DARK_MODE_LEGACY, Plan, TEXT_COLOUR};

    pub fn apply(handle: RawWindowHandle, plan: &Plan) -> bool {
        let RawWindowHandle::Win32(win32) = handle else {
            return false;
        };
        let window = HWND(win32.hwnd.get() as *mut core::ffi::c_void);

        if !set(window, IMMERSIVE_DARK_MODE, plan.dark) {
            set(window, IMMERSIVE_DARK_MODE_LEGACY, plan.dark);
        }
        let caption = set(window, CAPTION_COLOUR, plan.caption);
        let text = set(window, TEXT_COLOUR, plan.text);
        let border = set(window, BORDER_COLOUR, plan.border);

        caption || text || border
    }

    /// One attribute. Failure is silent: older Windows simply does not have most of them.
    fn set(window: HWND, attribute: u32, value: u32) -> bool {
        // SAFETY: `window` is the handle winit gave us for a live window, and `value` is a local
        // that outlives the call; the size passed is its size.
        unsafe {
            DwmSetWindowAttribute(
                window,
                DWMWINDOWATTRIBUTE(attribute as i32),
                std::ptr::from_ref(&value).cast(),
                size_of::<u32>() as u32,
            )
        }
        .is_ok()
    }
}

#[cfg(not(windows))]
mod platform {
    use raw_window_handle::RawWindowHandle;

    use super::Plan;

    /// No title bar colour off Windows: the window manager owns the decorations.
    pub fn apply(_handle: RawWindowHandle, _plan: &Plan) -> bool {
        false
    }
}

#[cfg(test)]
mod tests {
    use super::super::theme_catalog::ThemeCatalog;
    use super::*;

    #[test]
    fn a_colorref_is_blue_green_red() {
        assert_eq!(0x00CC_BBAA, color_ref(Color::rgb(0xAA, 0xBB, 0xCC)));
        assert_eq!(0, color_ref(Color::BLACK));
        assert_eq!(0x00FF_FFFF, color_ref(Color::WHITE));
    }

    #[test]
    fn the_title_bar_takes_the_frame_colour_the_text_and_the_line() {
        let retro = ThemeCatalog::find(Some("retro"));
        let plan = Plan::of(retro);

        // Retro frames the window in blue, not the black of its panels.
        assert_eq!(color_ref(Color::hex("#0000AA")), plan.caption);
        assert_ne!(color_ref(retro.panel), plan.caption);
        assert_eq!(color_ref(retro.text), plan.text);
        assert_eq!(color_ref(retro.line), plan.border);
        assert_eq!(1, plan.dark);

        let daylight = ThemeCatalog::find(Some("daylight"));
        assert_eq!(0, Plan::of(daylight).dark);
        assert_eq!(color_ref(daylight.panel), Plan::of(daylight).caption);
    }

    #[test]
    fn the_hint_follows_the_theme() {
        let dark = viewport_command(ThemeCatalog::find(Some("midnight")));
        let light = viewport_command(ThemeCatalog::find(Some("paper")));
        assert!(matches!(dark, ViewportCommand::SetTheme(SystemTheme::Dark)));
        assert!(matches!(light, ViewportCommand::SetTheme(SystemTheme::Light)));
    }

    #[test]
    fn a_window_without_a_win32_handle_is_left_alone() {
        use raw_window_handle::{RawWindowHandle, XlibWindowHandle};

        let handle = RawWindowHandle::Xlib(XlibWindowHandle::new(1));
        assert!(!platform::apply(handle, &Plan::of(ThemeCatalog::default_theme())));
    }
}
