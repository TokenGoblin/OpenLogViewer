//! Ported from `GaugeCatalog.cs`: the gauge definitions out of a firmware INI.
//!
//! Worth taking from the file rather than inventing: the firmware already says
//! what every channel is called, what it is measured in, what range is sensible and
//! where the readings become a problem. rusEFI defines 276 of these across 30
//! categories, with a redline that follows the tune's own rev limit. Guessing any
//! of it would produce a dial that looks right and is not.

use super::msq_ini::{self, SymbolSet, default_symbols};
use super::tuning_context;
use crate::fmt;
use crate::model::IgnoreCaseMap;
use crate::units::{self, UnitSystem};
use std::collections::BTreeMap;

/// Where a reading falls, for colouring.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum GaugeBand {
    Unknown,
    Normal,
    Warning,
    Danger,
}

/// One gauge as the firmware defines it: what to show, over what range, and where
/// the readings stop being good news.
#[derive(Clone, Debug, PartialEq)]
pub struct GaugeSpec {
    /// The name the INI gives this configuration, e.g. "CLTGauge".
    pub name: String,
    /// The output channel it displays.
    pub channel: String,
    pub title: String,
    pub units: String,
    /// Category from the preceding `gaugeCategory` line, for grouping.
    pub category: String,
    pub low: f64,
    pub high: f64,
    pub low_danger: f64,
    pub low_warning: f64,
    pub high_warning: f64,
    pub high_danger: f64,
    /// Decimals on the reading.
    pub value_digits: i32,
    /// Decimals on the scale labels.
    pub label_digits: i32,
}

impl GaugeSpec {
    /// A gauge with the C#'s defaults: no units or category, a range of zero, and
    /// every limit unstated (NaN).
    pub fn new(name: impl Into<String>, channel: impl Into<String>, title: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            channel: channel.into(),
            title: title.into(),
            units: String::new(),
            category: String::new(),
            low: 0.0,
            high: 0.0,
            low_danger: f64::NAN,
            low_warning: f64::NAN,
            high_warning: f64::NAN,
            high_danger: f64::NAN,
            value_digits: 0,
            label_digits: 0,
        }
    }

    /// Whether there is a range to draw a dial over.
    ///
    /// False for the several gauges whose generator left the bounds at zero: "Fuel
    /// Cut Code" and "Knock Frequency Start" among them. They are kept rather than
    /// dropped, because the channel is worth seeing even when the firmware has not
    /// said what a normal value looks like; they show as a reading without a face.
    pub fn has_scale(&self) -> bool {
        self.high > self.low
    }

    /// Whether the warning and danger bands are usable.
    ///
    /// Generated INIs often fill all six numbers with the same pair, which reads as
    /// a danger band covering everything. A band is only believed when it is
    /// ordered the way its names say it should be.
    pub fn has_bands(&self) -> bool {
        self.has_scale()
            && !self.low_danger.is_nan()
            && !self.low_warning.is_nan()
            && !self.high_warning.is_nan()
            && !self.high_danger.is_nan()
            && self.low_danger <= self.low_warning
            && self.low_warning <= self.high_warning
            && self.high_warning <= self.high_danger
    }

    /// Where a reading falls, for colouring.
    ///
    /// A limit has to sit inside the scale to mean anything, and one that does not
    /// is how these files say "no limit here".
    ///
    /// At the near end: a throttle runs 0 to 100 with its lower limits at 0, and
    /// read literally that paints a closed throttle as a fault.
    ///
    /// At the far end: a MicroSquirt's lost-sync counter runs 0 to 255 with all four
    /// of its limits at 255, meaning it has none. Read literally, a low-danger
    /// threshold of 255 puts every possible reading below it: the gauge was
    /// entirely red while reporting a perfectly healthy zero.
    pub fn band_for(&self, value: f64) -> GaugeBand {
        if value.is_nan() {
            return GaugeBand::Unknown;
        }
        if !self.has_bands() {
            return GaugeBand::Normal;
        }

        if self.inside(self.low_danger) && value <= self.low_danger {
            return GaugeBand::Danger;
        }
        if self.inside(self.high_danger) && value >= self.high_danger {
            return GaugeBand::Danger;
        }

        if self.inside(self.low_warning) && value <= self.low_warning {
            return GaugeBand::Warning;
        }
        if self.inside(self.high_warning) && value >= self.high_warning {
            return GaugeBand::Warning;
        }

        GaugeBand::Normal
    }

    /// Whether a limit falls within the scale, and so says anything at all.
    fn inside(&self, limit: f64) -> bool {
        limit > self.low && limit < self.high
    }

    /// The same gauge with its face in another system of units.
    ///
    /// The scale and every limit move with the reading, or a coolant gauge in
    /// Fahrenheit would keep a redline set in Celsius and call 100 degrees an
    /// emergency. Returns a copy of itself when nothing would change, so a gauge
    /// whose units this does not recognise is left exactly as the firmware wrote it.
    pub fn in_system(&self, system: UnitSystem) -> GaugeSpec {
        if !units::converts(&self.units, system) {
            return self.clone();
        }

        let units = &self.units;
        GaugeSpec {
            units: units::label(units, system),
            low: units::value(self.low, units, system),
            high: units::value(self.high, units, system),
            low_danger: units::value(self.low_danger, units, system),
            low_warning: units::value(self.low_warning, units, system),
            high_warning: units::value(self.high_warning, units, system),
            high_danger: units::value(self.high_danger, units, system),
            ..self.clone()
        }
    }

    /// Where the four bands begin and end along the dial, 0 to 1, for painting
    /// (low danger, low warning, high warning, high danger).
    ///
    /// A limit that says nothing is pushed to the end it sits beyond, which makes
    /// the band it would have bounded empty. Painting the raw numbers instead is how
    /// a lost-sync counter with all four limits at the top of its scale came to be
    /// drawn entirely red while reading zero.
    ///
    /// Kept here rather than in the drawing so the face and the reading cannot
    /// disagree about where the bands are: they did, and the dial was red while the
    /// number on it was not.
    pub fn band_edges(&self) -> (f64, f64, f64, f64) {
        (
            self.fraction(if self.inside(self.low_danger) {
                self.low_danger
            } else {
                self.low
            }),
            self.fraction(if self.inside(self.low_warning) {
                self.low_warning
            } else {
                self.low
            }),
            self.fraction(if self.inside(self.high_warning) {
                self.high_warning
            } else {
                self.high
            }),
            self.fraction(if self.inside(self.high_danger) {
                self.high_danger
            } else {
                self.high
            }),
        )
    }

    /// Where along the dial a reading sits, 0 to 1, clamped to the face.
    pub fn fraction(&self, value: f64) -> f64 {
        if value.is_nan() || self.high <= self.low {
            return 0.0;
        }

        ((value - self.low) / (self.high - self.low)).clamp(0.0, 1.0)
    }
}

/// Every gauge the INI defines, in file order.
///
/// `tune_settings` resolves bounds written as expressions: a rev counter runs to
/// `{rpmHardLimit + 2000}` and warns at `{rpmHardLimit - 500}`, so without the tune
/// the dial has no scale.
pub fn read(ini_text: &str, tune_settings: Option<&IgnoreCaseMap<f64>>, symbols: Option<&SymbolSet>) -> Vec<GaugeSpec> {
    let mut gauges: Vec<GaugeSpec> = Vec::new();
    let mut by_name: IgnoreCaseMap<usize> = IgnoreCaseMap::new();
    let mut category = String::new();

    for raw in msq_ini::section(
        ini_text,
        "GaugeConfigurations",
        symbols.unwrap_or_else(|| default_symbols()),
    ) {
        let line = msq_ini::strip(&raw);
        if line.is_empty() {
            continue;
        }

        let Some(equals) = line.find('=') else {
            continue;
        };

        let name = line[..equals].trim();
        let body = line[equals + 1..].trim();

        if name.eq_ignore_ascii_case("gaugeCategory") {
            category = unquote(body);
            continue;
        }

        let Some(gauge) = parse(name, body, &category, tune_settings) else {
            continue;
        };

        // A name defined twice (which happens where a Celsius and a Fahrenheit
        // variant both survive) takes its later definition, as any later assignment
        // would.
        match by_name.get(&gauge.name) {
            Some(&at) => gauges[at] = gauge,
            None => {
                by_name.insert(&gauge.name, gauges.len());
                gauges.push(gauge);
            }
        }
    }

    gauges
}

/// The dashboard the firmware suggests: `gauge1` to `gaugeN` of `[FrontPage]`, in
/// position order, as gauge configuration names.
pub fn read_front_page(ini_text: &str, symbols: Option<&SymbolSet>) -> Vec<String> {
    let mut positions: BTreeMap<i32, String> = BTreeMap::new();

    for raw in msq_ini::section(ini_text, "FrontPage", symbols.unwrap_or_else(|| default_symbols())) {
        let line = msq_ini::strip(&raw);

        let Some(equals) = line.find('=') else {
            continue;
        };

        let name = line[..equals].trim();
        if !msq_ini::starts_with_ignore_case(name, "gauge") {
            continue;
        }

        let Some(at) = name.char_indices().nth(5).and_then(|(i, _)| fmt::parse_i32(&name[i..])) else {
            continue;
        };

        let value = unquote(line[equals + 1..].trim());
        if !value.is_empty() {
            positions.insert(at, value);
        }
    }

    positions.into_values().collect()
}

fn parse(name: &str, body: &str, category: &str, settings: Option<&IgnoreCaseMap<f64>>) -> Option<GaugeSpec> {
    let fields = split_fields(body);

    // Channel, title, units and six bounds; the two digit counts are often present
    // but a gauge is usable without them.
    if fields.len() < 9 {
        return None;
    }

    let mut low = number(&fields[3], settings);
    let mut high = number(&fields[4], settings);

    // A bound that will not resolve leaves the gauge without a face rather than
    // without an entry; see `has_scale`. Both ends go, not just the one that failed:
    // half a range is not a range, and the half that survives makes a dial that
    // looks perfectly valid and is not. A MicroSquirt's coolant gauge reads "-40,
    // {clthighlim}", and with that variable unset it drew a scale of -40 to 0 with
    // the needle pegged off the end at 90.
    if low.is_nan() || high.is_nan() {
        low = 0.0;
        high = 0.0;
    }

    Some(GaugeSpec {
        name: name.to_string(),
        channel: unquote(&fields[0]),
        title: unquote(&fields[1]),
        units: units_in(&fields[2]),
        category: category.to_string(),
        low,
        high,
        low_danger: number(&fields[5], settings),
        low_warning: number(&fields[6], settings),
        high_warning: number(&fields[7], settings),
        high_danger: number(&fields[8], settings),
        value_digits: if fields.len() > 9 { digits(&fields[9]) } else { 0 },
        label_digits: if fields.len() > 10 { digits(&fields[10]) } else { 0 },
    })
}

/// Splits on commas that separate fields, ignoring those inside quotes or braces. A
/// bound can be an expression, and an expression can call a function with arguments
/// of its own.
fn split_fields(body: &str) -> Vec<String> {
    let mut fields = Vec::new();
    let mut start = 0;
    let mut braces = 0_i32;
    let mut quoted = false;

    for (i, c) in body.char_indices() {
        if c == '"' {
            quoted = !quoted;
        } else if !quoted && c == '{' {
            braces += 1;
        } else if !quoted && c == '}' {
            braces -= 1;
        } else if c == ',' && !quoted && braces <= 0 {
            fields.push(body[start..i].trim().to_string());
            start = i + 1;
        }
    }

    fields.push(body[start..].trim().to_string());

    fields
}

/// A bound: a plain number, or an expression over the tune's settings.
fn number(text: &str, settings: Option<&IgnoreCaseMap<f64>>) -> f64 {
    let trimmed = text.trim();
    if trimmed.is_empty() {
        return f64::NAN;
    }

    if let Some(plain) = fmt::parse_f64_inv(trimmed) {
        return plain;
    }

    if !trimmed.starts_with('{') || !trimmed.ends_with('}') {
        return f64::NAN;
    }
    let Some(settings) = settings else {
        return f64::NAN;
    };

    tuning_context::evaluate(trimmed[1..trimmed.len() - 1].trim(), settings).unwrap_or(f64::NAN)
}

fn digits(text: &str) -> i32 {
    fmt::parse_i32(text.trim()).map_or(0, |d| d.clamp(0, 6))
}

/// The units label, or nothing when the INI computes it.
///
/// A load gauge is labelled `{ bitStringValue(algorithmUnits, algorithm) }`: it says
/// kPa or %, depending on how the engine measures load. Printing the expression
/// instead is worse than printing nothing, so an unresolved one leaves the gauge
/// unlabelled rather than showing its own source.
fn units_in(field: &str) -> String {
    let units = unquote(field);

    if units.starts_with('{') { String::new() } else { units }
}

fn unquote(text: &str) -> String {
    let trimmed = text.trim();

    if trimmed.len() >= 2 && trimmed.starts_with('"') && trimmed.ends_with('"') {
        trimmed[1..trimmed.len() - 1].to_string()
    } else {
        trimmed.to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ini::ini_catalog;
    use crate::ini::tuning_context;
    use crate::model::LogChannel;

    /// Shaped like a real INI: bounds by expression, a category, and a front page.
    const INI: &str = r##"[PcVariables]
rpmhigh = scalar, U16, "rpm", 1, 0, 0, 30000, 0
clt_exp = bits, U08, [0:0], "Normal", "Expanded"

[OutputChannels]
ochBlockSize = 8
rpm     = scalar, U16, 0, "RPM",  1.0, 0.0
coolant = scalar, S16, 2, "deg F", 0.1, 0.0
clthighlim = { clt_exp ? 450 : 250 }

[GaugeConfigurations]
gaugeCategory = "Engine"
tachometer = rpm, "Engine Speed", "RPM", 0, {rpmhigh}, 300, 600, {rpmhigh - 1500}, {rpmhigh - 500}, 0, 0
cltGauge   = coolant, "Coolant Temp", "deg F", -40, {clthighlim}, -100, -100, 200, 220, 1, 1
gaugeCategory = "Diagnostics"
cutGauge   = fuelCutReason, "Fuel Cut Code", "code", 0.0,0.0, 0.0,0.0, 0.0,0.0, 0,0
loadGauge  = fuelload, "Fuel Load", { bitStringValue(algorithmUnits, algorithm) }, 0, 400, 0, 20, 200, 400, 1, 0

[FrontPage]
gauge1 = tachometer
gauge3 = cutGauge
gauge2 = cltGauge
"##;

    const TUNE: &str = r##"<?xml version="1.0" encoding="ISO-8859-1"?>
<msq xmlns="http://www.msefi.com/:msq">
<page>
<pcVariable name="rpmhigh">7000</pcVariable>
<pcVariable name="clt_exp">"Normal"</pcVariable>
<constant name="nCylinders">6</constant>
</page>
</msq>"##;

    fn gauges() -> Vec<GaugeSpec> {
        read(
            INI,
            Some(&tuning_context::build(Some(INI), Some(TUNE), None, None)),
            None,
        )
    }

    fn named(name: &str) -> GaugeSpec {
        let matching: Vec<GaugeSpec> = gauges().into_iter().filter(|g| g.name == name).collect();
        assert_eq!(matching.len(), 1, "{name}");
        matching.into_iter().next().unwrap()
    }

    // ----- the fields -------------------------------------------------------

    #[test]
    fn a_gauge_carries_its_channel_title_and_units() {
        let clt = named("cltGauge");

        assert_eq!(clt.channel, "coolant");
        assert_eq!(clt.title, "Coolant Temp");
        assert_eq!(clt.units, "deg F");
        assert_eq!(clt.value_digits, 1);
    }

    #[test]
    fn gauges_keep_the_category_they_were_listed_under() {
        assert_eq!(named("tachometer").category, "Engine");
        assert_eq!(named("cutGauge").category, "Diagnostics");
    }

    // ----- bounds that are expressions --------------------------------------

    /// The redline follows the engine, so it has to come out of the tune rather
    /// than out of a constant in this program.
    #[test]
    fn a_bound_written_as_an_expression_is_resolved_from_the_tune() {
        let tacho = named("tachometer");

        assert_eq!(tacho.high, 7000.0);
        assert_eq!(tacho.high_warning, 5500.0);
        assert_eq!(tacho.high_danger, 6500.0);
    }

    /// clt_exp is written in the tune as the word "Normal"; only the INI's
    /// declaration says that means zero, and only then does the coolant gauge get a
    /// top of 250 rather than 450.
    #[test]
    fn an_enum_stored_as_its_label_still_reaches_the_arithmetic() {
        assert_eq!(named("cltGauge").high, 250.0);
    }

    #[test]
    fn a_derived_channel_that_depends_on_nothing_live_is_available_as_a_bound() {
        let known = tuning_context::build(Some(INI), Some(TUNE), None, None);

        assert_eq!(known.get("clthighlim"), Some(&250.0));
    }

    /// Better than inventing a scale: a dial with the wrong range reads as a
    /// measurement rather than as missing information.
    #[test]
    fn without_a_tune_the_expression_bounds_simply_do_not_resolve() {
        let all = read(INI, None, None);

        assert!(!all.iter().find(|g| g.name == "tachometer").unwrap().has_scale());
    }

    // ----- bands ------------------------------------------------------------

    #[test]
    fn an_ordered_set_of_limits_counts_as_bands() {
        assert!(named("tachometer").has_bands());
    }

    #[test]
    fn readings_are_sorted_into_their_band() {
        let tacho = named("tachometer");

        assert_eq!(tacho.band_for(3000.0), GaugeBand::Normal);
        assert_eq!(tacho.band_for(5800.0), GaugeBand::Warning);
        assert_eq!(tacho.band_for(6800.0), GaugeBand::Danger);
        assert_eq!(tacho.band_for(100.0), GaugeBand::Danger);
        assert_eq!(tacho.band_for(f64::NAN), GaugeBand::Unknown);
    }

    /// A throttle runs 0 to 100 with its lower limits at 0. Read literally that
    /// paints a closed throttle red, which it was doing.
    #[test]
    fn a_limit_on_the_end_of_the_scale_means_there_is_no_limit_there() {
        let ini = "[GaugeConfigurations]\nthrottleGauge = tps, \"Throttle\", \"%\", 0, 100, 0, 0, 100, 100, 1, 0\n";

        let all = read(ini, None, None);
        assert_eq!(all.len(), 1);
        let throttle = &all[0];

        assert_eq!(throttle.band_for(0.0), GaugeBand::Normal);
        assert_eq!(throttle.band_for(50.0), GaugeBand::Normal);
        assert_eq!(throttle.band_for(100.0), GaugeBand::Normal);
    }

    /// The same rule must not disarm a limit that means something: coolant runs to
    /// 140 and is in trouble at 110.
    #[test]
    fn a_limit_inside_the_scale_still_bites() {
        let clt = named("cltGauge");

        assert_eq!(clt.band_for(240.0), GaugeBand::Danger);
        assert_eq!(clt.band_for(210.0), GaugeBand::Warning);
        assert_eq!(clt.band_for(90.0), GaugeBand::Normal);
    }

    /// Generated INIs repeat one pair across all six numbers, which taken at face
    /// value paints every reading as dangerous.
    #[test]
    fn limits_filled_in_with_the_same_pair_are_not_treated_as_bands() {
        let cut = named("cutGauge");

        assert!(!cut.has_bands());
        assert_eq!(cut.band_for(3.0), GaugeBand::Normal);
    }

    // ----- gauges without a face --------------------------------------------

    /// "Fuel Cut Code" is worth showing even though the firmware never said what a
    /// normal one looks like.
    #[test]
    fn a_gauge_with_no_range_is_kept_but_has_no_scale() {
        let cut = named("cutGauge");

        assert!(!cut.has_scale());
        assert_eq!(cut.title, "Fuel Cut Code");
        assert_eq!(cut.fraction(500.0), 0.0);
    }

    #[test]
    fn a_computed_units_label_is_left_blank_rather_than_printed() {
        assert_eq!(named("loadGauge").units, "");
    }

    // ----- the front page ---------------------------------------------------

    #[test]
    fn the_front_page_comes_back_in_position_order_not_file_order() {
        assert_eq!(read_front_page(INI, None), ["tachometer", "cltGauge", "cutGauge"]);
    }

    #[test]
    fn a_conditional_picks_one_gauge_for_a_position() {
        let ini = "[FrontPage]\n#if LAMBDA\ngauge1 = lambda1Gauge\n#else\ngauge1 = afr1Gauge\n#endif\n";

        assert_eq!(read_front_page(ini, None), ["afr1Gauge"]);
        assert_eq!(
            read_front_page(ini, Some(&SymbolSet::ordinal(["LAMBDA"]))),
            ["lambda1Gauge"]
        );
    }

    // ----- the project's own tune -------------------------------------------

    fn scratch(prefix: &str) -> std::path::PathBuf {
        use std::sync::atomic::{AtomicU32, Ordering};
        static COUNTER: AtomicU32 = AtomicU32::new(0);
        let n = COUNTER.fetch_add(1, Ordering::Relaxed);
        std::env::temp_dir().join(format!("{prefix}-{}-{n}", std::process::id()))
    }

    /// A MegaSquirt tachometer runs to {rpmhigh}, which is TunerStudio's variable
    /// rather than the firmware's: not in the ECU, not derivable, and absent from a
    /// tune exported on its own. It is in the project.
    #[test]
    fn the_project_tune_is_found_beside_the_firmware_definition() {
        let project = scratch("olv-proj");
        let configuration = project.join("projectCfg");
        std::fs::create_dir_all(&configuration).unwrap();

        let ini = configuration.join("mainController.ini");
        let tune = project.join("CurrentTune.msq");
        std::fs::write(&ini, "[MegaTune]\nsignature = \"test\"\n").unwrap();
        std::fs::write(&tune, "<msq/>").unwrap();

        let found = ini_catalog::project_tune_for(&ini.to_string_lossy());
        let _ = std::fs::remove_dir_all(&project);

        assert_eq!(found, Some(tune.to_string_lossy().into_owned()));
    }

    /// The ones under ecuDef are not part of a project, and inventing a path beside
    /// them would read whatever happened to be there.
    #[test]
    fn a_plain_firmware_definition_has_no_project_tune() {
        let folder = scratch("olv-ecudef");
        std::fs::create_dir_all(&folder).unwrap();

        let ini = folder.join("MS3Format0569.ini");
        std::fs::write(&ini, "[MegaTune]\n").unwrap();
        std::fs::write(folder.join("CurrentTune.msq"), "<msq/>").unwrap();

        let found = ini_catalog::project_tune_for(&ini.to_string_lossy());
        let _ = std::fs::remove_dir_all(&folder);

        assert_eq!(found, None);
    }

    #[test]
    fn a_project_with_no_tune_yields_nothing() {
        let project = scratch("olv-proj-empty");
        let configuration = project.join("projectCfg");
        std::fs::create_dir_all(&configuration).unwrap();

        let ini = configuration.join("mainController.ini");
        std::fs::write(&ini, "[MegaTune]\n").unwrap();

        let found = ini_catalog::project_tune_for(&ini.to_string_lossy());
        let _ = std::fs::remove_dir_all(&project);

        assert_eq!(found, None);
    }

    /// TunerStudio writes these with units and digits attributes, which a reader
    /// looking for a bare name attribute misses entirely.
    #[test]
    fn pc_variables_with_attributes_are_still_read() {
        let tune = "<?xml version=\"1.0\"?>\n<msq><page>\n<pcVariable digits=\"0\" name=\"rpmhigh\" units=\"rpm\">9000.0</pcVariable>\n</page></msq>";

        assert_eq!(
            tuning_context::read_pc_variables(Some(tune))
                .get("rpmhigh")
                .map(String::as_str),
            Some("9000.0")
        );
    }

    // ----- the dial ---------------------------------------------------------

    #[test]
    fn readings_map_onto_the_face_and_stay_on_it() {
        let tacho = named("tachometer");

        assert!((tacho.fraction(3500.0) - 0.5).abs() < 1e-6);
        assert_eq!(tacho.fraction(-1000.0), 0.0);
        assert_eq!(tacho.fraction(99999.0), 1.0);
        assert_eq!(tacho.fraction(f64::NAN), 0.0);
    }

    // ----- bounds that only half resolve ------------------------------------

    /// A MicroSquirt's coolant gauge, verbatim. One end is a plain number and the
    /// other is a tuning-software variable, so with no tune open only half of it
    /// resolves.
    const HALF_RESOLVED: &str = r##"[OutputChannels]
ochBlockSize = 4
coolant = scalar, S16, 0, "F", 0.1, 0.0
synccnt = scalar, U08, 2, "", 1.0, 0.0

[GaugeConfigurations]
cltGauge = coolant, "Coolant Temp", "°F", -40, {clthighlim}, {cltlowdang}, {cltlowwarn}, {clthighwarn}, {clthighdang}, 0, 0
lostsyncGauge = synccnt, "Lost Sync Counter", "", 0, 255, 255, 255, 255, 255, 0, 0
"##;

    fn half_resolved(title: &str) -> GaugeSpec {
        let all = read(HALF_RESOLVED, Some(&IgnoreCaseMap::new()), None);
        all.into_iter().find(|g| g.title == title).expect(title)
    }

    /// Not -40 to 0, which is what taking the end that did resolve gives: a dial
    /// that looks perfectly valid, with the needle pegged off the end at a coolant
    /// temperature of 90.
    #[test]
    fn a_half_resolved_range_is_no_range_at_all() {
        let clt = half_resolved("Coolant Temp");

        assert!(!clt.has_scale(), "drew a scale of {} to {}", clt.low, clt.high);
    }

    /// These files say "this gauge has no limits" by putting all four of them on one
    /// end. Read literally, a low-danger threshold of 255 puts every reading on a
    /// 0-255 scale below it: the gauge was entirely red while reporting a perfectly
    /// healthy zero.
    #[test]
    fn a_limit_beyond_the_far_end_of_the_scale_is_no_limit() {
        let sync = half_resolved("Lost Sync Counter");

        assert!(sync.has_scale());
        assert_eq!(sync.band_for(0.0), GaugeBand::Normal);
        assert_eq!(sync.band_for(128.0), GaugeBand::Normal);
        assert_eq!(sync.band_for(255.0), GaugeBand::Normal);
    }

    /// They did not: the dial was painted from the raw limits and the reading
    /// coloured from the ones that mean something, so a lost-sync counter showed a
    /// black zero on an entirely red face.
    #[test]
    fn the_face_and_the_reading_agree_about_where_the_bands_are() {
        let sync = half_resolved("Lost Sync Counter");

        let (low_danger, low_warning, high_warning, high_danger) = sync.band_edges();

        assert_eq!(low_danger, 0.0);
        assert_eq!(low_warning, 0.0);
        assert_eq!(high_warning, 1.0);
        assert_eq!(high_danger, 1.0);
    }

    #[test]
    fn a_face_with_real_limits_still_paints_them() {
        let tacho = named("tachometer");

        let (low_danger, low_warning, high_warning, high_danger) = tacho.band_edges();

        assert!(low_danger > 0.0 && low_danger < 1.0, "low danger at {low_danger}");
        assert!(low_danger <= low_warning);
        assert!(low_warning < high_warning);
        assert!(high_warning < high_danger);
        assert!(high_danger < 1.0, "high danger at {high_danger}");
    }

    /// The fix must not throw away the limits that mean something. Taken from the
    /// gauge's own thresholds rather than from numbers written out here, which only
    /// restate what the fixture happens to say.
    #[test]
    fn a_limit_inside_the_scale_still_counts() {
        let tacho = named("tachometer");

        assert!(tacho.has_bands());
        assert!(
            tacho.high_warning < tacho.high_danger,
            "the fixture has no room between the bands"
        );

        assert_eq!(tacho.band_for(tacho.high_danger + 1.0), GaugeBand::Danger);
        assert_eq!(
            tacho.band_for((tacho.high_warning + tacho.high_danger) / 2.0),
            GaugeBand::Warning
        );
        assert_eq!(
            tacho.band_for((tacho.low_warning + tacho.high_warning) / 2.0),
            GaugeBand::Normal
        );
    }

    // ----- UnitConvertTests --------------------------------------------------

    /// `Assert.Equal(expected, actual, precision)`: equal after rounding both to
    /// `precision` decimal places.
    fn near(expected: f64, actual: f64, precision: i32) {
        let scale = 10f64.powi(precision);
        assert_eq!(
            (expected * scale).round_ties_even(),
            (actual * scale).round_ties_even(),
            "{expected} vs {actual}"
        );
    }

    #[test]
    fn celsius_becomes_fahrenheit() {
        for (c, f) in [(0.0, 32.0), (100.0, 212.0), (-40.0, -40.0), (90.0, 194.0)] {
            near(f, units::value(c, "°C", UnitSystem::Imperial), 6);
            near(c, units::value(f, "°F", UnitSystem::Metric), 6);
        }
    }

    /// A mile is 1,609.344 metres exactly, so this is not an approximation.
    #[test]
    fn speed_converts_on_the_defined_mile() {
        near(62.137119, units::value(100.0, "km/h", UnitSystem::Imperial), 5);
        near(100.0, units::value(62.137119, "mph", UnitSystem::Metric), 4);
    }

    /// A MegaSquirt reports manifold pressure as an absolute figure, so an
    /// atmosphere is 14.5 psi rather than zero boost. Turning absolute into gauge
    /// silently would put a whole atmosphere of error on a boost reading, which on a
    /// turbocharged engine is the difference between nothing and fifteen pounds.
    #[test]
    fn pressure_converts_absolute_to_absolute() {
        near(14.5038, units::value(100.0, "kPa", UnitSystem::Imperial), 3);
        near(100.0, units::value(14.5038, "psi", UnitSystem::Metric), 3);

        assert_eq!(units::label("kPa", UnitSystem::Imperial), "psi");
        assert_eq!(units::label("psi", UnitSystem::Metric), "kPa");
    }

    /// 200 kPa absolute is one atmosphere of boost, which a boost gauge shows as
    /// about 14.5 psi and an absolute reading as 29.
    #[test]
    fn two_bar_of_boost_reads_as_thirty_psi_either_way() {
        near(29.0075, units::value(200.0, "kPa", UnitSystem::Imperial), 3);
    }

    #[test]
    fn converting_to_the_system_a_reading_is_already_in_changes_nothing() {
        assert_eq!(units::value(90.0, "°C", UnitSystem::Metric), 90.0);
        assert_eq!(units::value(90.0, "°F", UnitSystem::Imperial), 90.0);
        assert_eq!(units::value(100.0, "km/h", UnitSystem::Metric), 100.0);
    }

    /// The one setting that cannot be wrong, and the default for that reason.
    #[test]
    fn as_reported_is_untouched() {
        assert_eq!(units::value(90.0, "°C", UnitSystem::AsReported), 90.0);
        assert_eq!(units::label("°C", UnitSystem::AsReported), "°C");
    }

    /// Firmware shipped by the same people writes all of these.
    #[test]
    fn the_same_unit_written_any_of_the_usual_ways_is_recognised() {
        for units in ["°C", "C", "deg C", "degC", "celsius"] {
            near(212.0, units::value(100.0, units, UnitSystem::Imperial), 6);
        }
    }

    /// The dangerous direction. Reading "degrees" of ignition advance as a
    /// temperature would put 32 degrees of timing on a gauge that said zero, and an
    /// unfamiliar unit is a far smaller problem than a wrong number.
    #[test]
    fn a_unit_that_is_not_a_temperature_or_a_speed_is_left_alone() {
        // "degrees" is ignition advance on every ECU here; "°" is OBD2's timing
        // advance; "TEMP" is Speeduino's placeholder for "whichever the tune says".
        for units in ["degrees", "°", "TEMP", "%", "rpm", "g/s", "V", ""] {
            assert_eq!(units::value(0.0, units, UnitSystem::Imperial), 0.0);
            assert_eq!(units::value(37.0, units, UnitSystem::Metric), 37.0);
            assert_eq!(units::label(units, UnitSystem::Imperial), units);
            assert!(!units::converts(units, UnitSystem::Imperial));
        }
    }

    #[test]
    fn nothing_to_read_is_still_nothing_to_read() {
        assert!(units::value(f64::NAN, "°C", UnitSystem::Imperial).is_nan());
    }

    fn coolant() -> GaugeSpec {
        let mut spec = GaugeSpec::new("cltGauge", "coolant", "Coolant Temp");
        spec.units = "°C".to_string();
        spec.low = -40.0;
        spec.high = 120.0;
        spec.low_danger = -20.0;
        spec.low_warning = 0.0;
        spec.high_warning = 100.0;
        spec.high_danger = 110.0;
        spec
    }

    /// A dial drawn in Fahrenheit keeping a redline set in Celsius would call 100
    /// degrees an emergency.
    #[test]
    fn the_whole_face_moves_with_the_reading() {
        let imperial = coolant().in_system(UnitSystem::Imperial);

        assert_eq!(imperial.units, "°F");
        near(-40.0, imperial.low, 6);
        near(248.0, imperial.high, 6);
        near(212.0, imperial.high_warning, 6);
        near(230.0, imperial.high_danger, 6);
    }

    /// The real test of it: converting the face and the reading together must not
    /// change what the gauge is saying.
    #[test]
    fn a_reading_falls_in_the_same_band_whichever_way_it_is_shown() {
        let metric = coolant();
        let imperial = metric.in_system(UnitSystem::Imperial);

        for celsius in [-30.0, -10.0, 20.0, 90.0, 105.0, 115.0] {
            assert_eq!(
                metric.band_for(celsius),
                imperial.band_for(units::value(celsius, "°C", UnitSystem::Imperial))
            );
        }
    }

    #[test]
    fn a_gauge_in_units_this_does_not_know_is_handed_back_untouched() {
        let mut advance = coolant();
        advance.units = "degrees".to_string();

        assert_eq!(advance.in_system(UnitSystem::Imperial), advance);
    }

    /// Converting zero to zero must not accidentally invent a range.
    #[test]
    fn a_faceless_gauge_stays_faceless() {
        let mut unknown = coolant();
        unknown.low = 0.0;
        unknown.high = 0.0;

        assert!(!unknown.in_system(UnitSystem::Imperial).has_scale());
    }

    #[test]
    fn a_channel_reads_in_the_chosen_system() {
        let channel = LogChannel::new("Coolant", "°C", 0, &[0.0, 50.0, 100.0], false);

        assert_eq!(channel.format_with_units(100.0, UnitSystem::AsReported), "100 °C");
        assert_eq!(channel.format_with_units(100.0, UnitSystem::Imperial), "212 °F");
        assert_eq!(channel.format_with_units(100.0, UnitSystem::Metric), "100 °C");
    }

    #[test]
    fn channels_in_other_units_are_unaffected_by_switching() {
        let rpm = LogChannel::new("RPM", "rpm", 0, &[0.0, 3000.0], false);

        assert_eq!(rpm.format_with_units(3000.0, UnitSystem::Imperial), "3000 rpm");
    }

    // ----- back the other way -----------------------------------------------

    #[test]
    fn a_typed_value_comes_back_in_the_units_the_log_uses() {
        for (units, from, typed, expected) in [
            ("C", UnitSystem::Imperial, 212.0, 100.0),
            ("C", UnitSystem::Imperial, 32.0, 0.0),
            ("F", UnitSystem::Metric, 100.0, 212.0),
            ("kph", UnitSystem::Imperial, 100.0, 160.9344),
            ("mph", UnitSystem::Metric, 160.9344, 100.0),
            ("kpa", UnitSystem::Imperial, 14.503773773020923, 100.0),
        ] {
            near(expected, units::to_reported(typed, units, from), 6);
        }
    }

    /// The round trip a pinned scale makes: seeded into the editor in the units on
    /// screen, typed back, stored raw. A conversion that did not invert exactly
    /// would move a pinned range every time it was reopened.
    #[test]
    fn showing_a_value_and_typing_it_back_gives_the_same_number() {
        for (units, system, raw) in [
            ("C", UnitSystem::Imperial, 87.5),
            ("F", UnitSystem::Metric, 187.5),
            ("kph", UnitSystem::Imperial, 137.5),
            ("mph", UnitSystem::Metric, 87.5),
            ("kpa", UnitSystem::Imperial, 137.5),
            ("psi", UnitSystem::Metric, 17.5),
            ("rpm", UnitSystem::Imperial, 4500.0),
            ("", UnitSystem::Metric, 12.5),
        ] {
            let shown = units::value(raw, units, system);

            near(raw, units::to_reported(shown, units, system), 9);
        }
    }

    #[test]
    fn as_reported_converts_neither_way() {
        assert_eq!(units::value(90.0, "C", UnitSystem::AsReported), 90.0);
        assert_eq!(units::to_reported(90.0, "C", UnitSystem::AsReported), 90.0);
    }

    #[test]
    fn a_missing_reading_stays_missing_coming_back() {
        assert!(units::to_reported(f64::NAN, "C", UnitSystem::Imperial).is_nan());
    }
}
