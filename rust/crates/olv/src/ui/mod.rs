//! `ui`: see `rust/packages.toml` for which package owns each file.
//!
//! The foundation every screen builds on: the theme tokens and their colour maths, fonts, corner
//! radii, the interface zoom, the title bar colour and PNG export, and the glue that turns a theme
//! into egui's style ([`egui_theme`], the one module here with no C# counterpart).

pub mod color_math;
pub mod egui_theme;
pub mod image_export;
pub mod interface_scale;
pub mod mvvm;
pub mod theme;
pub mod theme_catalog;
pub mod theme_fonts;
pub mod theme_manager;
pub mod title_bar;
pub mod workspace_mode;

pub use color_math::Color;
pub use interface_scale::InterfaceScale;
pub use theme::{Corner, TableStyle, Theme, ThemeGroup, ThemeStyle};
pub use theme_catalog::ThemeCatalog;
pub use theme_fonts::{ThemeFont, ThemeFonts};
pub use theme_manager::ThemeManager;
pub use workspace_mode::{LogView, WorkspaceMode};
