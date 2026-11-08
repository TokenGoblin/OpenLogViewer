//! `olv-themes`: every theme at a glance, before the real application exists.
//!
//! Shows each theme's swatches (stated and derived), a sample tune table in both styles (heat and
//! grid), text in both fonts, and the standard widgets, with a theme picker and the interface zoom.
//! Retro is the reason it exists: it is the theme that changes more than colours.
//!
//! ```text
//! olv-themes [--theme <id>] [--zoom <x>] [--settings <file>] [--screenshot <png>] [--scroll <px>]
//! ```
//!
//! * `--theme` and `--zoom` apply for this run only and are not stored.
//! * The theme and zoom picked in the window are stored in `--settings` (default: a file in the
//!   system temporary folder, so the real application's settings are never touched).
//! * `--scroll <px>` starts the page scrolled down, to screenshot its lower sections.
//! * `--screenshot <png>` saves the window after it has settled and exits; it is how the
//!   screenshots for review are made.

#![cfg_attr(all(windows, not(debug_assertions)), windows_subsystem = "windows")]

use std::path::PathBuf;

use eframe::egui::{self, Align2, Context, Pos2, Rect, RichText, Sense, Stroke, StrokeKind, Ui, vec2};
use olv::ui::color_math::{self as cm, Color};
use olv::ui::image_export::{self, Ticket};
use olv::ui::interface_scale::{self, InterfaceScale};
use olv::ui::title_bar;
use olv::ui::{Corner, TableStyle, Theme, ThemeCatalog, ThemeFonts, ThemeManager};
use olv_store::store::settings_store::SettingsStore;

/// The frames to wait before a requested screenshot: fonts and sizes are applied at the start of
/// the pass after they are set, so the first few frames are still settling.
const SETTLE_FRAMES: u32 = 6;

#[derive(Default)]
struct Args {
    theme: Option<String>,
    zoom: Option<f64>,
    settings: Option<PathBuf>,
    screenshot: Option<PathBuf>,
    scroll: f32,
}

fn parse_args() -> Args {
    let mut args = Args::default();
    let mut it = std::env::args().skip(1);
    while let Some(arg) = it.next() {
        match arg.as_str() {
            "--theme" => args.theme = it.next(),
            "--zoom" => args.zoom = it.next().and_then(|v| v.parse().ok()),
            "--settings" => args.settings = it.next().map(PathBuf::from),
            "--screenshot" => args.screenshot = it.next().map(PathBuf::from),
            "--scroll" => args.scroll = it.next().and_then(|v| v.parse().ok()).unwrap_or(0.0),
            other => eprintln!("olv-themes: ignoring {other}"),
        }
    }
    args
}

fn main() -> eframe::Result {
    let args = parse_args();

    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_title("OpenLogViewer: themes")
            .with_inner_size([1240.0, 860.0])
            .with_min_inner_size([760.0, 520.0]),
        ..Default::default()
    };

    eframe::run_native(
        "olv-themes",
        options,
        Box::new(move |creation| Ok(Box::new(ThemesApp::new(&creation.egui_ctx, args)))),
    )
}

struct ThemesApp {
    settings: SettingsStore,
    first_scroll: Option<f32>,
    screenshot: Option<PathBuf>,
    ticket: Option<Ticket>,
    frame: u32,
    /// The theme generation the title bar was last coloured for.
    title_bar_generation: Option<u64>,
    status: String,
    show_hex: bool,
    selected_cell: (usize, usize),
    text_box: String,
    slider: f32,
    checked: bool,
    toggled: bool,
    combo: usize,
}

impl ThemesApp {
    fn new(ctx: &Context, args: Args) -> Self {
        let settings_path = args
            .settings
            .unwrap_or_else(|| std::env::temp_dir().join("olv-themes-settings.json"));
        let settings = SettingsStore::new(settings_path);

        // Before the first frame, so none is drawn in another theme or at another size. A flag
        // applies for this run only; the stored values are the fallback.
        match &args.theme {
            Some(id) => {
                ThemeManager::preview(ctx, id);
            }
            None => {
                ThemeManager::apply_stored(ctx, &settings);
            }
        }
        match args.zoom {
            Some(zoom) => {
                InterfaceScale::preview(ctx, zoom);
            }
            None => {
                InterfaceScale::apply(ctx, InterfaceScale::stored(&settings));
            }
        }

        Self {
            settings,
            first_scroll: (args.scroll > 0.0).then_some(args.scroll),
            screenshot: args.screenshot,
            ticket: None,
            frame: 0,
            title_bar_generation: None,
            status: String::new(),
            show_hex: true,
            selected_cell: (3, 4),
            text_box: "VE table (rpm x kPa)".to_owned(),
            slider: 0.4,
            checked: true,
            toggled: true,
            combo: 1,
        }
    }

    fn choose_theme(&mut self, ctx: &Context, theme: &'static Theme) {
        if let Err(error) = ThemeManager::choose(ctx, &self.settings, theme) {
            self.status = format!("Could not store the theme: {error}");
        }
    }

    fn choose_zoom(&mut self, ctx: &Context, scale: f64) {
        match InterfaceScale::choose(ctx, &self.settings, scale) {
            Ok(snapped) => self.status = format!("Zoom {}", interface_scale::label(snapped)),
            Err(error) => self.status = format!("Could not store the zoom: {error}"),
        }
    }
}

impl eframe::App for ThemesApp {
    fn logic(&mut self, ctx: &Context, _frame: &mut eframe::Frame) {
        // Before anything is drawn: re-sizes pixel text if the density changed.
        ThemeManager::begin_frame(ctx);

        if let Some(scale) = InterfaceScale::handle_shortcuts(ctx) {
            self.choose_zoom(ctx, scale);
        }
    }

    fn ui(&mut self, ui: &mut Ui, frame: &mut eframe::Frame) {
        let ctx = ui.ctx().clone();
        let theme = ThemeManager::current(&ctx);

        // The title bar follows the theme: once when the window exists, and whenever it changes.
        let generation = ThemeManager::generation(&ctx);
        if self.title_bar_generation != Some(generation) {
            title_bar::apply(&ctx, frame, theme);
            self.title_bar_generation = Some(generation);
        }

        self.frame += 1;
        self.drive_screenshot(&ctx, theme);

        self.menu_bar(ui, theme);
        self.status_bar(ui, theme);
        self.theme_list(ui, theme);

        egui::CentralPanel::default()
            .frame(egui::Frame::new().fill(theme.background.to_color32()).inner_margin(12))
            .show(ui, |ui| {
                let mut area = egui::ScrollArea::both().auto_shrink(false);
                if let Some(y) = self.first_scroll.take() {
                    area = area.vertical_scroll_offset(y);
                }
                olv::ui::egui_theme::scroll_area(ui, theme, area, |ui| {
                    self.page(ui, theme);
                });
            });
    }
}

// ----- chrome -------------------------------------------------------------------------------

impl ThemesApp {
    fn menu_bar(&mut self, ui: &mut Ui, theme: &'static Theme) {
        let ctx = ui.ctx().clone();
        egui::Panel::top("menu")
            .frame(egui::Frame::new().fill(theme.chrome().to_color32()).inner_margin(4))
            .show(ui, |ui| {
                egui::MenuBar::new().ui(ui, |ui| {
                    ui.menu_button("Theme", |ui| {
                        olv::ui::egui_theme::scroll_area(ui, theme, egui::ScrollArea::vertical(), |ui| {
                            for t in ThemeCatalog::themes() {
                                let current = t.id == theme.id;
                                if ui.selectable_label(current, t.name).clicked() {
                                    self.choose_theme(&ctx, t);
                                    ui.close();
                                }
                            }
                        });
                    });
                    ui.menu_button("View", |ui| {
                        for step in interface_scale::STEPS {
                            let current = (InterfaceScale::current(&ctx) - step).abs() < 1e-9;
                            if ui.selectable_label(current, interface_scale::label(step)).clicked() {
                                self.choose_zoom(&ctx, step);
                                ui.close();
                            }
                        }
                        ui.separator();
                        ui.checkbox(&mut self.show_hex, "Show colour values");
                    });
                    ui.menu_button("File", |ui| {
                        if ui.button("Save this page as PNG").clicked() {
                            let path = std::env::temp_dir().join(format!("olv-themes-{}.png", theme.id));
                            let view = ctx.content_rect();
                            self.ticket = Some(image_export::request(&ctx, view, path, Some(theme.background)));
                            ui.close();
                        }
                    });

                    ui.separator();

                    // The picker the toolbar would have.
                    egui::ComboBox::from_id_salt("theme-picker")
                        .selected_text(theme.name)
                        .show_ui(ui, |ui| {
                            for t in ThemeCatalog::themes() {
                                if ui.selectable_label(t.id == theme.id, t.name).clicked() {
                                    self.choose_theme(&ctx, t);
                                }
                            }
                        });

                    ui.separator();
                    let current = InterfaceScale::current(&ctx);
                    if ui.button("-").on_hover_text("Smaller (Ctrl -)").clicked() {
                        self.choose_zoom(&ctx, interface_scale::smaller(current));
                    }
                    ui.label(interface_scale::label(current));
                    if ui.button("+").on_hover_text("Larger (Ctrl =)").clicked() {
                        self.choose_zoom(&ctx, interface_scale::larger(current));
                    }
                    if ui.button("100%").on_hover_text("Actual size (Ctrl 0)").clicked() {
                        self.choose_zoom(&ctx, 1.0);
                    }
                });
            });
    }

    fn status_bar(&mut self, ui: &mut Ui, theme: &'static Theme) {
        let ctx = ui.ctx().clone();
        egui::Panel::bottom("status")
            .frame(egui::Frame::new().fill(theme.chrome().to_color32()).inner_margin(4))
            .show(ui, |ui| {
                ui.horizontal(|ui| {
                    let data = ThemeFonts::data(&ctx, theme, 13.0);
                    let style = &theme.style;
                    let text = format!(
                        "{}  |  {:?}  |  corners x{}  |  pixel text {}  |  table {:?}  |  zoom {}  |  {:.2} px/pt",
                        theme.name,
                        theme.group,
                        style.corner_scale,
                        if style.pixel_text { "on" } else { "off" },
                        style.table,
                        interface_scale::label(InterfaceScale::current(&ctx)),
                        ctx.pixels_per_point(),
                    );
                    ui.label(RichText::new(text).font(data).color(theme.readout().to_color32()));
                    if !self.status.is_empty() {
                        ui.label(RichText::new(&self.status).color(theme.muted.to_color32()));
                    }
                });
            });
    }

    fn theme_list(&mut self, ui: &mut Ui, theme: &'static Theme) {
        let ctx = ui.ctx().clone();
        egui::Panel::left("themes")
            .frame(egui::Frame::new().fill(theme.panel.to_color32()).inner_margin(8))
            .resizable(false)
            .show(ui, |ui| {
                ui.label(RichText::new("THEMES").color(theme.muted.to_color32()));
                ui.add_space(4.0);
                olv::ui::egui_theme::scroll_area(ui, theme, egui::ScrollArea::vertical(), |ui| {
                    for t in ThemeCatalog::themes() {
                        ui.horizontal(|ui| {
                            // A strip of the theme's own colours, so the picker previews it.
                            let (rect, _) = ui.allocate_exact_size(vec2(36.0, 14.0), Sense::hover());
                            let step = rect.width() / 4.0;
                            for (i, color) in [t.background, t.panel_alt, t.accent, t.series[0]]
                                .into_iter()
                                .enumerate()
                            {
                                let cell = Rect::from_min_size(
                                    Pos2::new(rect.min.x + step * i as f32, rect.min.y),
                                    vec2(step, rect.height()),
                                );
                                ui.painter().rect_filled(cell, 0.0, color.to_color32());
                            }
                            ui.painter().rect_stroke(
                                rect,
                                0.0,
                                Stroke::new(1.0, theme.line.to_color32()),
                                StrokeKind::Inside,
                            );
                            if ui.selectable_label(t.id == theme.id, t.name).clicked() {
                                self.choose_theme(&ctx, t);
                            }
                        });
                    }
                });
            });
    }

    /// A requested screenshot waits for the window to settle, saves, then closes the window.
    fn drive_screenshot(&mut self, ctx: &Context, theme: &Theme) {
        if let Some(path) = &self.screenshot
            && self.ticket.is_none()
            && self.frame >= SETTLE_FRAMES
        {
            self.ticket = Some(image_export::request(
                ctx,
                ctx.content_rect(),
                path.clone(),
                Some(theme.background),
            ));
        }

        if let Some(ticket) = &self.ticket
            && let Some(result) = ticket.take()
        {
            match result {
                Ok(path) => self.status = format!("Saved {}", path.display()),
                Err(error) => self.status = format!("Could not save: {error}"),
            }
            if self.screenshot.is_some() {
                eprintln!("{}", self.status);
                ctx.send_viewport_cmd(egui::ViewportCommand::Close);
            }
            self.ticket = None;
        } else if self.ticket.is_some() {
            ctx.request_repaint();
        }
    }
}

// ----- the page -----------------------------------------------------------------------------

impl ThemesApp {
    fn page(&mut self, ui: &mut Ui, theme: &'static Theme) {
        let ctx = ui.ctx().clone();
        ui.set_min_width(900.0);

        let heading = ThemeFonts::ui(&ctx, theme, 24.0);
        ui.label(RichText::new(theme.name).font(heading).color(theme.text.to_color32()));
        ui.label(
            RichText::new(format!(
                "id {}  |  {:?}  |  {}",
                theme.id,
                theme.group,
                if theme.is_dark() { "dark" } else { "light" }
            ))
            .color(theme.muted.to_color32()),
        );
        ui.add_space(14.0);

        section(ui, theme, "Stated colours");
        let stated: [(&str, Color); 10] = [
            ("Background", theme.background),
            ("Panel", theme.panel),
            ("PanelAlt", theme.panel_alt),
            ("Line", theme.line),
            ("Text", theme.text),
            ("Muted", theme.muted),
            ("Accent", theme.accent),
            ("Marker", theme.marker),
            ("RampCool", theme.ramp_cool),
            ("RampWarm", theme.ramp_warm),
        ];
        self.swatches(ui, theme, &stated);

        section(ui, theme, "Derived colours");
        let derived: [(&str, Color); 17] = [
            ("Readout", theme.readout()),
            ("Chrome", theme.chrome()),
            ("Grid", theme.grid()),
            ("Hover", theme.hover()),
            ("Faint", theme.faint()),
            ("Scroll", theme.scroll()),
            ("ScrollHover", theme.scroll_hover()),
            ("Selected", theme.selected()),
            ("OnAccent", theme.on_accent()),
            ("Header", theme.header()),
            ("Lane", theme.lane()),
            ("Cursor", theme.cursor()),
            ("Card", theme.card()),
            ("EmptyCell", theme.empty_cell()),
            ("Warning", theme.warning()),
            ("Danger", theme.danger()),
            ("Nominal", theme.nominal()),
        ];
        self.swatches(ui, theme, &derived);

        section(ui, theme, "Trace colours (Series)");
        let names: Vec<String> = (1..=theme.series.len()).map(|i| i.to_string()).collect();
        let series: Vec<(&str, Color)> = names.iter().map(String::as_str).zip(theme.series).collect();
        self.swatches(ui, theme, &series);

        section(
            ui,
            theme,
            "Sequential ramp, and the two diverging arms (cool, then warm)",
        );
        self.swatch_row(ui, theme, "ramp", &theme.sequential_ramp());
        self.swatch_row(ui, theme, "cool", &theme.cool_arm());
        self.swatch_row(ui, theme, "warm", &theme.warm_arm());

        section(ui, theme, "Tune table: heat fill and ruled grid");
        ui.label(
            RichText::new(format!(
                "This theme's tune table is drawn in the {:?} style.",
                theme.style.table
            ))
            .color(theme.muted.to_color32()),
        );
        ui.add_space(6.0);
        ui.horizontal_wrapped(|ui| {
            ui.vertical(|ui| {
                ui.label(RichText::new("Heat").color(theme.muted.to_color32()));
                self.table(ui, theme, TableStyle::Heat, "heat");
            });
            ui.add_space(24.0);
            ui.vertical(|ui| {
                ui.label(RichText::new("Grid").color(theme.muted.to_color32()));
                self.table(ui, theme, TableStyle::Grid, "grid");
            });
        });

        section(ui, theme, "Text");
        self.text_samples(ui, theme);

        section(ui, theme, "Controls");
        self.controls(ui, theme);

        ui.add_space(40.0);
    }

    fn swatches(&self, ui: &mut Ui, theme: &Theme, colours: &[(&str, Color)]) {
        ui.horizontal_wrapped(|ui| {
            for (name, color) in colours {
                self.swatch(ui, theme, name, *color);
            }
        });
    }

    fn swatch_row(&self, ui: &mut Ui, theme: &Theme, name: &str, colours: &[Color]) {
        ui.horizontal(|ui| {
            ui.add_sized(
                vec2(48.0, 20.0),
                egui::Label::new(RichText::new(name).color(theme.muted.to_color32())),
            );
            for (i, color) in colours.iter().enumerate() {
                self.swatch(ui, theme, &i.to_string(), *color);
            }
        });
    }

    fn swatch(&self, ui: &mut Ui, theme: &Theme, name: &str, color: Color) {
        let ctx = ui.ctx().clone();
        let (rect, response) = ui.allocate_exact_size(vec2(92.0, 52.0), Sense::hover());
        let painter = ui.painter();
        let radius = theme.radius(Corner::Small) as f32;
        painter.rect_filled(rect, radius, color.to_color32());
        painter.rect_stroke(
            rect,
            radius,
            Stroke::new(1.0, theme.line.to_color32()),
            StrokeKind::Inside,
        );

        // Ink for the label: whichever of the theme's text and ground reads on this swatch.
        let ink = cm::readable(color, theme.text, theme.background).to_color32();
        painter.text(
            rect.left_top() + vec2(6.0, 6.0),
            Align2::LEFT_TOP,
            name,
            ThemeFonts::ui(&ctx, theme, 11.0),
            ink,
        );
        if self.show_hex {
            painter.text(
                rect.left_bottom() + vec2(6.0, -6.0),
                Align2::LEFT_BOTTOM,
                color.to_string(),
                ThemeFonts::data(&ctx, theme, 11.0),
                ink,
            );
        }
        response.on_hover_text(format!("{name} {color}"));
    }

    /// A tune table, drawn the way the real one will be: cells in the data font, either filled
    /// from the sequential ramp by value (heat) or unfilled on a ruled grid in the readout ink.
    fn table(&mut self, ui: &mut Ui, theme: &Theme, style: TableStyle, id: &str) {
        let ctx = ui.ctx().clone();
        const COLUMNS: usize = 9;
        const ROWS: usize = 8;
        let rpm = [800, 1200, 1600, 2000, 2800, 3600, 4400, 5200, 6000];
        let kpa = [35, 45, 55, 65, 75, 85, 95, 105];

        let font = ThemeFonts::data(&ctx, theme, 13.0);
        let cell = vec2(54.0, 24.0);
        let header = vec2(50.0, 24.0);
        let size = vec2(header.x + cell.x * COLUMNS as f32, cell.y * (ROWS + 1) as f32);
        let (rect, _) = ui.allocate_exact_size(size, Sense::hover());
        let painter = ui.painter_at(rect);

        painter.rect_filled(rect, 0.0, theme.background.to_color32());
        let ramp = theme.sequential_ramp();

        // A smooth made-up volumetric efficiency surface.
        let value = |row: usize, col: usize| -> f64 {
            let x = col as f64 / (COLUMNS - 1) as f64;
            let y = row as f64 / (ROWS - 1) as f64;
            38.0 + 52.0 * (0.55 * x + 0.45 * y).powf(0.9) + 6.0 * (x * 5.0).sin() * (y * 3.0).cos()
        };
        let (mut low, mut high) = (f64::MAX, f64::MIN);
        for r in 0..ROWS {
            for c in 0..COLUMNS {
                low = low.min(value(r, c));
                high = high.max(value(r, c));
            }
        }

        // Column heads (rpm).
        for (c, rpm) in rpm.iter().enumerate() {
            let at = Rect::from_min_size(Pos2::new(rect.min.x + header.x + cell.x * c as f32, rect.min.y), cell);
            painter.text(
                at.center(),
                Align2::CENTER_CENTER,
                rpm.to_string(),
                font.clone(),
                theme.muted.to_color32(),
            );
        }

        for (r, kpa) in kpa.iter().enumerate() {
            let y = rect.min.y + cell.y * (r + 1) as f32;
            painter.text(
                Pos2::new(rect.min.x + header.x - 8.0, y + cell.y / 2.0),
                Align2::RIGHT_CENTER,
                kpa.to_string(),
                font.clone(),
                theme.muted.to_color32(),
            );

            for c in 0..COLUMNS {
                let at = Rect::from_min_size(Pos2::new(rect.min.x + header.x + cell.x * c as f32, y), cell);
                let v = value(r, c);
                let t = ((v - low) / (high - low)).clamp(0.0, 1.0);

                let ink = match style {
                    TableStyle::Heat => {
                        let fill = ramp_at(&ramp, t);
                        painter.rect_filled(at.shrink(1.0), 0.0, fill.to_color32());
                        cm::readable(fill, theme.text, theme.background)
                    }
                    TableStyle::Grid => theme.readout(),
                };
                painter.text(
                    at.center(),
                    Align2::CENTER_CENTER,
                    format!("{v:.1}"),
                    font.clone(),
                    ink.to_color32(),
                );
            }
        }

        // The rules: the theme's grid colour. Grid tables are made of them.
        if style == TableStyle::Grid {
            let rules = Stroke::new(1.0, theme.grid().to_color32());
            for r in 0..=ROWS {
                let y = rect.min.y + cell.y * (r + 1).min(ROWS + 1) as f32;
                painter.hline(rect.min.x + header.x..=rect.max.x, y.min(rect.max.y), rules);
            }
            painter.hline(rect.min.x + header.x..=rect.max.x, rect.min.y, rules);
            for c in 0..=COLUMNS {
                let x = rect.min.x + header.x + cell.x * c as f32;
                painter.vline(x.min(rect.max.x), rect.min.y..=rect.max.y, rules);
            }
        }

        // The cell being edited: Accent border.
        let (sr, sc) = self.selected_cell;
        let selected = Rect::from_min_size(
            Pos2::new(
                rect.min.x + header.x + cell.x * sc as f32,
                rect.min.y + cell.y * (sr + 1) as f32,
            ),
            cell,
        );
        painter.rect_stroke(
            selected,
            0.0,
            Stroke::new(2.0, theme.accent.to_color32()),
            StrokeKind::Inside,
        );

        // Click to move the selection.
        let response = ui.interact(rect, ui.make_persistent_id(id), Sense::click());
        if let Some(p) = response.interact_pointer_pos() {
            let local = p - rect.min;
            if local.x >= header.x && local.y >= cell.y {
                let c = ((local.x - header.x) / cell.x) as usize;
                let r = ((local.y - cell.y) / cell.y) as usize;
                if c < COLUMNS && r < ROWS {
                    self.selected_cell = (r, c);
                }
            }
        }
    }

    fn text_samples(&self, ui: &mut Ui, theme: &Theme) {
        let ctx = ui.ctx().clone();
        let text = theme.text.to_color32();
        let muted = theme.muted.to_color32();
        let readout = theme.readout().to_color32();

        ui.label(RichText::new("UI font").color(muted));
        for size in [11.0, 13.0, 18.0, 24.0] {
            ui.label(
                RichText::new(format!(
                    "{size:.0} px   Boost pressure, lambda target, closed-loop fuel trim"
                ))
                .font(ThemeFonts::ui(&ctx, theme, size))
                .color(text),
            );
        }
        ui.add_space(8.0);

        ui.label(RichText::new("Data font").color(muted));
        for size in [11.0, 13.0, 18.0] {
            ui.label(
                RichText::new(format!(
                    "{size:.0} px   RPM 3456  MAP 101.3 kPa  AFR 14.70  IAT -12.5  TPS 100.0%"
                ))
                .font(ThemeFonts::data(&ctx, theme, size))
                .color(readout),
            );
        }
        ui.add_space(8.0);

        ui.label(RichText::new("Symbols the pixel font draws itself").color(muted));
        ui.label(
            RichText::new(
                "\u{b0} \u{394} \u{3bb} \u{3c0} \u{b1} \u{d7} \u{f7} \u{2264} \u{2265} \u{2190} \u{2191} \u{2192} \u{2193} \u{2026} \u{25b2} \u{25bc} \u{25a0} \u{25cf} \u{2713} \u{2715}",
            )
            .font(ThemeFonts::ui(&ctx, theme, 18.0))
            .color(text),
        );
        ui.add_space(8.0);

        ui.label(RichText::new("Glyphs it lacks fall back to the standard face, one at a time").color(muted));
        ui.label(
            RichText::new("caf\u{e9} na\u{ef}ve \u{fc}ber \u{20ac} \u{e5}ngstr\u{f6}m")
                .font(ThemeFonts::ui(&ctx, theme, 18.0))
                .color(text),
        );
    }

    fn controls(&mut self, ui: &mut Ui, theme: &Theme) {
        ui.horizontal_wrapped(|ui| {
            let _ = ui.button("Button");
            ui.toggle_value(&mut self.toggled, "Toggle");
            ui.add_enabled(false, egui::Button::new("Disabled"));
            ui.checkbox(&mut self.checked, "Check box");
            ui.add(egui::TextEdit::singleline(&mut self.text_box).desired_width(180.0));
            let ecus = ["Speeduino", "MegaSquirt", "rusEFI", "MaxxECU"];
            egui::ComboBox::from_id_salt("sample-combo")
                .selected_text(ecus[self.combo])
                .show_ui(ui, |ui| {
                    for (i, name) in ecus.iter().enumerate() {
                        ui.selectable_value(&mut self.combo, i, *name);
                    }
                });
            ui.add(egui::Slider::new(&mut self.slider, 0.0..=1.0).text("Slider"));
        });
        ui.add_space(8.0);

        ui.horizontal(|ui| {
            for (label, color) in [
                ("Warning", theme.warning()),
                ("Danger", theme.danger()),
                ("Nominal", theme.nominal()),
            ] {
                ui.label(RichText::new(format!("\u{25cf} {label}")).color(color.to_color32()));
            }
            ui.label(RichText::new("Marker").color(theme.marker.to_color32()));
            ui.hyperlink_to("Accent link", "https://github.com");
        });
        ui.add_space(8.0);

        ui.label(RichText::new("Scroll area (the thumb is the Scroll token)").color(theme.muted.to_color32()));
        egui::Frame::new()
            .fill(theme.panel.to_color32())
            .stroke(Stroke::new(1.0, theme.line.to_color32()))
            .inner_margin(6)
            .show(ui, |ui| {
                ui.set_width(420.0);
                olv::ui::egui_theme::scroll_area(
                    ui,
                    theme,
                    egui::ScrollArea::vertical()
                        .max_height(110.0)
                        .auto_shrink([false, true]),
                    |ui| {
                        for i in 1..=30 {
                            ui.label(format!("Channel {i:02}"));
                        }
                    },
                );
            });
    }
}

fn section(ui: &mut Ui, theme: &Theme, title: &str) {
    ui.add_space(18.0);
    ui.label(RichText::new(title.to_uppercase()).color(theme.muted.to_color32()));
    let (rect, _) = ui.allocate_exact_size(vec2(ui.available_width().min(900.0), 1.0), Sense::hover());
    ui.painter().rect_filled(rect, 0.0, theme.line.to_color32());
    ui.add_space(8.0);
}

/// A colour a fraction of the way along a ramp, blending between neighbouring steps.
fn ramp_at(ramp: &[Color], t: f64) -> Color {
    let position = t.clamp(0.0, 1.0) * (ramp.len() - 1) as f64;
    let low = position.floor() as usize;
    let high = (low + 1).min(ramp.len() - 1);
    cm::blend(ramp[low], ramp[high], position - low as f64)
}
