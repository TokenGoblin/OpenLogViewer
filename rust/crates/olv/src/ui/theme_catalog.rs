//! Ported from `ThemeCatalog.cs`. Owned by package `w6-ui-base`.
//!
//! The built-in colour schemes.
//!
//! Every `series` palette here was checked against its own background for lightness range,
//! chroma, contrast, and separation of adjacent entries under protanopia and deuteranopia. The
//! editor schemes therefore do not use their upstream syntax colours verbatim: those sit too
//! light and too close together to tell two traces apart when they cross. Each palette keeps its
//! scheme's hues and its relative saturation, and moves only in lightness.

use super::color_math::Color;
use super::theme::{TableStyle, Theme, ThemeGroup, ThemeStyle};
use super::theme_fonts::ThemeFont;

pub const DEFAULT_ID: &str = "midnight";

const fn c(hex: &str) -> Color {
    Color::hex(hex)
}

const fn palette(list: [&str; 8]) -> [Color; 8] {
    let mut out = [Color::BLACK; 8];
    let mut i = 0;
    while i < 8 {
        out[i] = c(list[i]);
        i += 1;
    }
    out
}

/// A theme that states only colours: no optional ink, and the modern style.
#[allow(clippy::too_many_arguments)]
const fn scheme(
    id: &'static str,
    name: &'static str,
    group: ThemeGroup,
    [background, panel, panel_alt, line, text, muted, accent, marker]: [&str; 8],
    series: [&str; 8],
    [ramp_cool, ramp_warm]: [&str; 2],
) -> Theme {
    Theme {
        id,
        name,
        group,
        background: c(background),
        panel: c(panel),
        panel_alt: c(panel_alt),
        line: c(line),
        text: c(text),
        muted: c(muted),
        accent: c(accent),
        marker: c(marker),
        series: palette(series),
        ramp_cool: c(ramp_cool),
        ramp_warm: c(ramp_warm),
        style: ThemeStyle::MODERN,
        readout_override: None,
        chrome_override: None,
        grid_override: None,
    }
}

static ALL: [Theme; 15] = [
    scheme(
        "midnight",
        "Midnight",
        ThemeGroup::Dark,
        [
            "#14171C", "#1B1F26", "#222831", "#2C333E", "#DDE3EA", "#828D9E", "#4FC3F7", "#FFB34D",
        ],
        [
            "#56820F", "#9D4CAB", "#B58D00", "#7582D5", "#DE5222", "#007FAA", "#BF3469", "#06A4B5",
        ],
        ["#3987E5", "#D03B3B"],
    ),
    scheme(
        "graphite",
        "Graphite",
        ThemeGroup::Dark,
        [
            "#17181A", "#1E1F22", "#26282C", "#34363B", "#E2E3E6", "#8B8E95", "#7EB6E8", "#E0A44A",
        ],
        [
            "#B8496A", "#2A7DB7", "#A87C05", "#7F86D3", "#55843F", "#7C5496", "#24A59D", "#E16A33",
        ],
        ["#4A8FD0", "#C4523F"],
    ),
    scheme(
        "daylight",
        "Daylight",
        ThemeGroup::Light,
        [
            "#FFFFFF", "#F4F6F8", "#E8ECF0", "#CDD5DD", "#1B2026", "#5A6672", "#0B6BCB", "#B25E00",
        ],
        [
            "#D32F69", "#007AD5", "#CC8600", "#8C22A8", "#2B7A2F", "#6D7ED5", "#E54918", "#23A3B1",
        ],
        ["#1E88E5", "#D0342C"],
    ),
    scheme(
        "paper",
        "Paper",
        ThemeGroup::Light,
        [
            "#FBF7EF", "#F3EEE3", "#EAE3D5", "#D8CFBD", "#2A241B", "#6B6152", "#A85A16", "#8A5A00",
        ],
        [
            "#31671B", "#8272EF", "#EA6900", "#1263BE", "#F55E35", "#7124A1", "#29A08E", "#AA0F55",
        ],
        ["#1565C0", "#C43B18"],
    ),
    scheme(
        "dracula",
        "Dracula",
        ThemeGroup::Editor,
        [
            "#282A36", "#21222C", "#343746", "#44475A", "#F8F8F2", "#9CA0B0", "#BD93F9", "#FFB86C",
        ],
        [
            "#D3519E", "#757B00", "#8563B3", "#008E37", "#687CBF", "#F64C4E", "#0090A5", "#C48132",
        ],
        ["#7B8FE8", "#FF5555"],
    ),
    scheme(
        "nord",
        "Nord",
        ThemeGroup::Editor,
        [
            "#2E3440", "#272C36", "#3B4252", "#434C5E", "#ECEFF4", "#A0A8B7", "#88C0D0", "#EBCB8B",
        ],
        [
            "#B18D40", "#008BA5", "#B66951", "#23A5A5", "#BE6069", "#5080BB", "#658842", "#A5689C",
        ],
        ["#5E81AC", "#BF616A"],
    ),
    scheme(
        "solarized-dark",
        "Solarized Dark",
        ThemeGroup::Editor,
        [
            "#002B36", "#01313D", "#073642", "#0E4A57", "#EEE8D5", "#93A1A1", "#268BD2", "#CB4B16",
        ],
        [
            "#687800", "#666ABD", "#D75624", "#2CA89F", "#DE3431", "#0075BA", "#B58901", "#C13D7A",
        ],
        ["#268BD2", "#DC322F"],
    ),
    scheme(
        "solarized-light",
        "Solarized Light",
        ThemeGroup::Editor,
        [
            "#FDF6E3", "#F5EED9", "#EEE8D5", "#DCD4BC", "#073642", "#657B83", "#268BD2", "#B58900",
        ],
        [
            "#738402", "#6368BA", "#E86636", "#1E9F96", "#E9403A", "#1380C6", "#C12073", "#B38806",
        ],
        ["#268BD2", "#DC322F"],
    ),
    scheme(
        "monokai",
        "Monokai",
        ThemeGroup::Editor,
        [
            "#272822", "#21221C", "#31322B", "#414339", "#F8F8F2", "#A6A28C", "#66D9EF", "#FD971F",
        ],
        [
            "#018D7A", "#FE2D76", "#7D61B4", "#5F8502", "#CC6286", "#A19528", "#0093A7", "#D07801",
        ],
        ["#4EA8C4", "#F92672"],
    ),
    scheme(
        "one-dark",
        "One Dark",
        ThemeGroup::Editor,
        [
            "#282C34", "#21252B", "#31363F", "#3E4451", "#CDD3DE", "#828997", "#61AFEF", "#D19A66",
        ],
        [
            "#B38C40", "#3779AD", "#DB6871", "#26A4B2", "#A2692C", "#7E8BD1", "#587F38", "#9660A6",
        ],
        ["#61AFEF", "#E06C75"],
    ),
    scheme(
        "gruvbox",
        "Gruvbox Dark",
        ThemeGroup::Editor,
        [
            "#282828", "#1D2021", "#32302F", "#504945", "#EBDBB2", "#A89984", "#83A598", "#FE8019",
        ],
        [
            "#737500", "#00989D", "#B35707", "#6FA05E", "#A75970", "#B78602", "#098165", "#E12B18",
        ],
        ["#458588", "#CC241D"],
    ),
    scheme(
        "tokyo-night",
        "Tokyo Night",
        ThemeGroup::Editor,
        [
            "#1A1B26", "#16161E", "#24283B", "#2F334D", "#C0CAF5", "#787C99", "#7AA2F7", "#FF9E64",
        ],
        [
            "#B4853D", "#486CBC", "#E1627B", "#27A3B4", "#B05714", "#4A9DCA", "#4B7501", "#8361B9",
        ],
        ["#7AA2F7", "#F7768E"],
    ),
    scheme(
        "contrast-dark",
        "High Contrast Dark",
        ThemeGroup::HighContrast,
        [
            "#000000", "#000000", "#1C1C1C", "#FFFFFF", "#FFFFFF", "#E0E0E0", "#00E0FF", "#FFD000",
        ],
        [
            "#00A899", "#FB494A", "#7B6BFE", "#038434", "#AC00C5", "#AF8E00", "#017ABA", "#B95600",
        ],
        ["#0088FF", "#FF3B30"],
    ),
    scheme(
        "contrast-light",
        "High Contrast Light",
        ThemeGroup::HighContrast,
        [
            "#FFFFFF", "#FFFFFF", "#EDEDED", "#000000", "#000000", "#2A2A2A", "#0032C8", "#8A3A00",
        ],
        [
            "#B88E42", "#8900B5", "#107234", "#717DFF", "#E7462F", "#0359DB", "#AD0067", "#21A3AD",
        ],
        ["#0032C8", "#C20000"],
    ),
    // A 1990s DOS tuning screen: black ground, a text-mode blue frame, cyan rules, neon magenta
    // for what is being acted on and phosphor green for the numbers. The first theme to set a
    // style as well as colours: the pixel font, square corners and a ruled tune table are as
    // much of the look as the palette is.
    Theme {
        readout_override: Some(c("#55FF55")),
        chrome_override: Some(c("#0000AA")),
        grid_override: Some(c("#1E3CC8")),
        style: ThemeStyle {
            ui_font: ThemeFont::Retro,
            data_font: ThemeFont::Retro,
            corner_scale: 0.0,
            pixel_text: true,
            table: TableStyle::Grid,
        },
        ..scheme(
            "retro",
            "Retro DOS",
            ThemeGroup::Retro,
            [
                "#000000", "#000000", "#00005A", "#38C8D8", "#F0F0F0", "#A8A8A8", "#FF40FF", "#FFFF55",
            ],
            [
                "#FF40FF", "#40E0FF", "#FFFF55", "#6A7BFF", "#55FF55", "#FF8C1A", "#20C0A0", "#FF4F6E",
            ],
            ["#2050FF", "#FF4A3A"],
        )
    },
];

/// The built-in themes, in picker order.
pub struct ThemeCatalog;

impl ThemeCatalog {
    pub fn themes() -> &'static [Theme] {
        &ALL
    }

    pub fn default_theme() -> &'static Theme {
        Self::find(Some(DEFAULT_ID))
    }

    /// The named theme (matched case-insensitively), or the default when the id is unknown or
    /// missing.
    pub fn find(id: Option<&str>) -> &'static Theme {
        id.and_then(|id| ALL.iter().find(|t| t.id.eq_ignore_ascii_case(id)))
            .unwrap_or_else(|| {
                ALL.iter()
                    .find(|t| t.id == DEFAULT_ID)
                    .expect("the default theme is in the catalog")
            })
    }
}
