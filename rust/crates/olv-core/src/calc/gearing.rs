//! Ported from `Gearing.cs`. Owned by package `w1-calc-b`.
//!
//! What gear a car is in, how fast that is, and what the engine is doing about
//! it. The arithmetic is a chain of ratios and one circumference; a rolling
//! tyre squats under the car and travels about three per cent less than pi
//! times its diameter per revolution, so that deflection is an input.

// The C# writes `!(x > 0)` so that NaN is refused; `x <= 0` would let it through.
#![allow(clippy::neg_cmp_op_on_partial_ord)]

use crate::fmt;

/// A tyre, as written on its sidewall.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Tyre {
    /// Section width, the first number.
    pub width_mm: f64,
    /// Sidewall height as a percentage of the width, the second.
    pub aspect_percent: f64,
    /// Wheel it fits, the last, which is still in inches.
    pub rim_inches: f64,
}

impl Tyre {
    /// Overall diameter: the rim, plus a sidewall at the top and another at the
    /// bottom.
    pub fn diameter_mm(&self) -> f64 {
        (self.rim_inches * 25.4) + (2.0 * self.width_mm * self.aspect_percent / 100.0)
    }

    pub fn diameter_inches(&self) -> f64 {
        self.diameter_mm() / 25.4
    }
}

impl std::fmt::Display for Tyre {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "{}/{}R{}",
            fmt::f64(self.width_mm, "N0"),
            fmt::f64(self.aspect_percent, "N0"),
            fmt::f64(self.rim_inches, "0.#")
        )
    }
}

/// How much less than its geometric circumference a loaded tyre actually
/// travels, as a percentage. A convention rather than a measurement.
pub const ROLLING_DEFLECTION_PERCENT: f64 = 3.0;

pub const MM_PER_MILE: f64 = 1_609_344.0;

pub const MM_PER_KM: f64 = 1_000_000.0;

/// The constant in the gear calculation everyone quotes (mph equals rpm times
/// tyre diameter in inches, over ratio times final drive times 336), computed
/// rather than transcribed so a cross-check agrees to all figures.
pub fn classic_mph_constant() -> f64 {
    63_360.0 / (std::f64::consts::PI * 60.0)
}

/// Digits at `chars[*i..]`, taking between `min` and `max` ASCII digits.
fn digits(chars: &[char], i: &mut usize, min: usize, max: usize) -> Option<String> {
    let start = *i;
    while *i < chars.len() && *i - start < max && chars[*i].is_ascii_digit() {
        *i += 1;
    }
    if *i - start < min {
        None
    } else {
        Some(chars[start..*i].iter().collect())
    }
}

fn skip_ws(chars: &[char], i: &mut usize) {
    while *i < chars.len() && chars[*i].is_whitespace() {
        *i += 1;
    }
}

/// Matches `^\s*[A-Z]{0,3}\s*(\d{2,3})\s*/\s*(\d{2,3})\s*(?:Z?R|-)\s*(\d{2}(?:\.\d)?)\s*$`
/// (ignoring case) by hand, since the pattern is deterministic.
fn parse_sidewall(text: &str) -> Option<Tyre> {
    let chars: Vec<char> = text.chars().collect();
    let mut i = 0;

    skip_ws(&chars, &mut i);

    let letters_start = i;
    while i < chars.len() && i - letters_start < 3 && chars[i].is_ascii_alphabetic() {
        i += 1;
    }
    skip_ws(&chars, &mut i);

    let width = digits(&chars, &mut i, 2, 3)?;
    skip_ws(&chars, &mut i);
    if chars.get(i) != Some(&'/') {
        return None;
    }
    i += 1;
    skip_ws(&chars, &mut i);

    let aspect = digits(&chars, &mut i, 2, 3)?;
    skip_ws(&chars, &mut i);

    match chars.get(i) {
        Some('-') => i += 1,
        Some(c) if c.eq_ignore_ascii_case(&'z') => {
            i += 1;
            if !chars.get(i)?.eq_ignore_ascii_case(&'r') {
                return None;
            }
            i += 1;
        }
        Some(c) if c.eq_ignore_ascii_case(&'r') => i += 1,
        _ => return None,
    }
    skip_ws(&chars, &mut i);

    let mut rim = digits(&chars, &mut i, 2, 2)?;
    if chars.get(i) == Some(&'.') && chars.get(i + 1).is_some_and(|c| c.is_ascii_digit()) {
        rim.push('.');
        rim.push(chars[i + 1]);
        i += 2;
    }
    skip_ws(&chars, &mut i);
    if i != chars.len() {
        return None;
    }

    Some(Tyre {
        width_mm: width.parse().ok()?,
        aspect_percent: aspect.parse().ok()?,
        rim_inches: rim.parse().ok()?,
    })
}

/// Reads a sidewall: "245/40R18", "P225/45ZR17", "245/40-18".
pub fn try_parse_tyre(text: Option<&str>) -> Option<Tyre> {
    let text = text?;
    if text.trim().is_empty() {
        return None;
    }
    parse_sidewall(text)
}

/// What the tyre actually covers in one turn, in millimetres.
pub fn rolling_circumference_mm(diameter_mm: f64, deflection_percent: f64) -> f64 {
    if diameter_mm.is_nan() || diameter_mm <= 0.0 {
        return f64::NAN;
    }

    std::f64::consts::PI * diameter_mm * (1.0 - (deflection_percent / 100.0))
}

fn mm_per_minute(rpm: f64, gear_ratio: f64, final_drive: f64, rolling_circumference_mm: f64) -> f64 {
    let drive = gear_ratio * final_drive;

    if !(drive > 0.0) || !(rpm > 0.0) || !(rolling_circumference_mm > 0.0) {
        return f64::NAN;
    }

    rpm / drive * rolling_circumference_mm
}

/// Road speed, in miles per hour: the engine turns, the gearbox divides it, the
/// final drive divides it again, and the rest turns the tyre once per
/// circumference of road.
pub fn mph(rpm: f64, gear_ratio: f64, final_drive: f64, rolling_circumference_mm: f64) -> f64 {
    mm_per_minute(rpm, gear_ratio, final_drive, rolling_circumference_mm) * 60.0 / MM_PER_MILE
}

/// Road speed, in kilometres per hour.
pub fn kph(rpm: f64, gear_ratio: f64, final_drive: f64, rolling_circumference_mm: f64) -> f64 {
    mm_per_minute(rpm, gear_ratio, final_drive, rolling_circumference_mm) * 60.0 / MM_PER_KM
}

/// What the engine is doing at a given road speed.
pub fn rpm_at(mph_: f64, gear_ratio: f64, final_drive: f64, rolling_circumference_mm: f64) -> f64 {
    if !(mph_ > 0.0) {
        return f64::NAN;
    }

    let per_rpm = mph(1_000.0, gear_ratio, final_drive, rolling_circumference_mm) / 1_000.0;

    if per_rpm > 0.0 { mph_ / per_rpm } else { f64::NAN }
}

/// One gear, and everything worth knowing about it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct GearStep {
    /// Its number, counting from one.
    pub gear: i32,
    /// Turns of the input per turn of the output.
    pub ratio: f64,
    /// Road speed at the redline.
    pub mph: f64,
    /// The same in kilometres per hour.
    pub kph: f64,
    /// How long-legged the gear is.
    pub mph_per_thousand_rpm: f64,
    /// Where the engine lands on taking the next gear at the redline.
    pub rpm_after_upshift: f64,
    /// What it turns at the cruising speed asked for.
    pub rpm_at_cruise: f64,
}

/// Every gear, at the redline.
///
/// `ratios` are in order, lowest gear first; `cruise_mph` is a road speed to
/// report engine speed at alongside (0 for none).
pub fn table(
    ratios: &[f64],
    final_drive: f64,
    redline_rpm: f64,
    rolling_circumference_mm: f64,
    cruise_mph: f64,
) -> Vec<GearStep> {
    if ratios.is_empty() || !(final_drive > 0.0) || !(redline_rpm > 0.0) {
        return Vec::new();
    }

    let mut steps = Vec::new();

    for (i, &ratio) in ratios.iter().enumerate() {
        if !(ratio > 0.0) {
            continue;
        }

        // Taking the next gear at the redline drops the engine in proportion to
        // how much taller that gear is.
        let after_upshift = if i + 1 < ratios.len() && ratios[i + 1] > 0.0 {
            redline_rpm * ratios[i + 1] / ratio
        } else {
            f64::NAN
        };

        steps.push(GearStep {
            gear: i as i32 + 1,
            ratio,
            mph: mph(redline_rpm, ratio, final_drive, rolling_circumference_mm),
            kph: kph(redline_rpm, ratio, final_drive, rolling_circumference_mm),
            mph_per_thousand_rpm: mph(1_000.0, ratio, final_drive, rolling_circumference_mm),
            rpm_after_upshift: after_upshift,
            rpm_at_cruise: if cruise_mph > 0.0 {
                rpm_at(cruise_mph, ratio, final_drive, rolling_circumference_mm)
            } else {
                f64::NAN
            },
        });
    }

    steps
}

/// The fastest the gearing will let the car go, which is rarely the fastest it
/// actually goes: the tallest gear at the redline and nothing else.
pub fn geared_top_speed_mph(ratios: &[f64], final_drive: f64, redline_rpm: f64, rolling_circumference_mm: f64) -> f64 {
    let steps = table(ratios, final_drive, redline_rpm, rolling_circumference_mm, 0.0);

    // LINQ Max on doubles: NaN is the smallest value.
    steps
        .iter()
        .map(|s| s.mph)
        .reduce(|a, b| if b > a || a.is_nan() { b } else { a })
        .unwrap_or(f64::NAN)
}

/// The gearing table as the chart the tab shows. Lines are joined with `\n`
/// (the C# used `Environment.NewLine`).
pub fn chart(steps: &[GearStep], with_cruise: bool) -> String {
    let mut lines = vec![if with_cruise {
        "gear   ratio     mph    km/h  /1000    next  @cruise".to_string()
    } else {
        "gear   ratio     mph    km/h  /1000    next".to_string()
    }];

    for s in steps {
        let next = if s.rpm_after_upshift.is_nan() {
            "\u{2014}".to_string()
        } else {
            fmt::f64(s.rpm_after_upshift, "N0")
        };
        let cruise = if s.rpm_at_cruise.is_nan() {
            "\u{2014}".to_string()
        } else {
            fmt::f64(s.rpm_at_cruise, "N0")
        };

        let mut line = format!(
            "{:>4}  {:>6}  {:>6}  {:>6}  {:>5}  {:>6}",
            s.gear,
            fmt::f64(s.ratio, "N3"),
            fmt::f64(s.mph, "N0"),
            fmt::f64(s.kph, "N0"),
            fmt::f64(s.mph_per_thousand_rpm, "N1"),
            next
        );
        if with_cruise {
            line.push_str(&format!("  {cruise:>7}"));
        }
        lines.push(line);
    }

    lines.join("\n")
}

#[cfg(test)]
mod tests {
    use super::*;

    const SIX_SPEED: [f64; 6] = [3.545, 2.048, 1.416, 1.059, 0.848, 0.756];

    fn close(expected: f64, actual: f64, digits: i32) {
        let tol = 0.5 * 10f64.powi(-digits);
        assert!(
            (expected - actual).abs() <= tol,
            "expected {expected}, got {actual} (to {digits} digits)"
        );
    }

    fn in_range(v: f64, lo: f64, hi: f64) {
        assert!(v >= lo && v <= hi, "{v} not in {lo}..={hi}");
    }

    fn rc(d: f64) -> f64 {
        rolling_circumference_mm(d, ROLLING_DEFLECTION_PERCENT)
    }

    fn tyre(s: &str) -> Tyre {
        try_parse_tyre(Some(s)).unwrap()
    }

    #[test]
    fn a_sidewall_is_read_the_way_it_is_written() {
        let cases = [
            ("245/40R18", 245.0, 40.0, 18.0),
            ("225/45ZR17", 225.0, 45.0, 17.0),
            ("P205/55R16", 205.0, 55.0, 16.0),
            ("315/30-19", 315.0, 30.0, 19.0),
            (" 245 / 40 r 18 ", 245.0, 40.0, 18.0),
        ];
        for (s, w, a, r) in cases {
            let t = try_parse_tyre(Some(s)).unwrap_or_else(|| panic!("{s}"));
            assert_eq!(w, t.width_mm);
            assert_eq!(a, t.aspect_percent);
            assert_eq!(r, t.rim_inches);
        }
    }

    #[test]
    fn anything_else_is_refused_rather_than_guessed_at() {
        for s in [
            None,
            Some(""),
            Some("   "),
            Some("26"),
            Some("not a tyre"),
            Some("245/40"),
        ] {
            assert!(try_parse_tyre(s).is_none(), "{s:?}");
        }
    }

    #[test]
    fn a_tyres_diameter_is_the_rim_plus_a_sidewall_at_each_end() {
        let t = tyre("245/40R18");
        close(653.2, t.diameter_mm(), 1);
        close(25.72, t.diameter_inches(), 2);
    }

    #[test]
    fn a_rolling_tyre_covers_less_ground_than_its_geometry_suggests() {
        let t = tyre("245/40R18");

        let geometric = std::f64::consts::PI * t.diameter_mm();
        let rolling = rc(t.diameter_mm());

        close(2_052.0, geometric, 0);
        in_range(rolling, 1_980.0, 2_010.0);

        let revs_per_mile = MM_PER_MILE / rolling;
        in_range(revs_per_mile, 795.0, 815.0);

        // With the deflection turned off it is exactly the geometry.
        close(geometric, rolling_circumference_mm(t.diameter_mm(), 0.0), 6);
    }

    #[test]
    fn the_speed_agrees_with_the_constant_everybody_quotes() {
        let t = tyre("245/40R18");
        let circumference = rolling_circumference_mm(t.diameter_mm(), 0.0);

        for (rpm, ratio, fin) in [(7_000.0, 0.756, 3.90), (3_000.0, 1.0, 4.10), (6_500.0, 3.545, 3.42)] {
            let classic = rpm * t.diameter_inches() / (ratio * fin * classic_mph_constant());
            close(classic, mph(rpm, ratio, fin, circumference), 6);
        }
    }

    #[test]
    fn a_familiar_car_sits_where_a_familiar_car_sits() {
        let t = tyre("245/40R18");
        let circumference = rc(t.diameter_mm());

        in_range(rpm_at(70.0, 0.756, 3.90, circumference), 2_700.0, 2_850.0);
        in_range(rpm_at(70.0, 0.63, 3.15, circumference), 1_800.0, 1_950.0);
    }

    #[test]
    fn speed_and_engine_speed_are_each_others_inverse() {
        let circumference = rc(650.0);

        for rpm in [1_000.0, 3_500.0, 7_200.0] {
            let m = mph(rpm, 1.31, 3.90, circumference);
            close(rpm, rpm_at(m, 1.31, 3.90, circumference), 6);
        }
    }

    #[test]
    fn kilometres_and_miles_describe_the_same_speed() {
        let circumference = rc(650.0);

        let m = mph(6_000.0, 1.0, 3.90, circumference);
        let k = kph(6_000.0, 1.0, 3.90, circumference);

        close(1.609344, k / m, 6);
    }

    #[test]
    fn a_taller_gear_is_faster_and_a_taller_final_drive_is_too() {
        let circumference = rc(650.0);

        assert!(mph(7_000.0, 0.756, 3.90, circumference) > mph(7_000.0, 1.310, 3.90, circumference));
        assert!(mph(7_000.0, 1.0, 3.42, circumference) > mph(7_000.0, 1.0, 4.10, circumference));
        assert!(mph(7_000.0, 1.0, 3.90, rc(700.0)) > mph(7_000.0, 1.0, 3.90, circumference));
    }

    #[test]
    fn the_deflection_is_worth_three_per_cent_of_the_speedometer() {
        let geometric = rolling_circumference_mm(653.2, 0.0);
        let rolling = rolling_circumference_mm(653.2, 3.0);

        let fast = mph(7_000.0, 1.0, 3.90, geometric);
        let honest = mph(7_000.0, 1.0, 3.90, rolling);

        close(0.03, 1.0 - (honest / fast), 6);
        close(0.030928, (fast / honest) - 1.0, 6);
    }

    #[test]
    fn every_gear_is_faster_than_the_one_before_it() {
        let circumference = rc(653.2);
        let t = table(&SIX_SPEED, 3.90, 7_000.0, circumference, 0.0);

        assert_eq!(6, t.len());

        for i in 1..t.len() {
            assert!(t[i].mph > t[i - 1].mph, "gear {} was not faster", i + 1);
            assert!(t[i].mph_per_thousand_rpm > t[i - 1].mph_per_thousand_rpm);
            assert_eq!(i as i32 + 1, t[i].gear);
        }
    }

    #[test]
    fn an_upshift_drops_the_engine_by_the_ratio_between_the_gears() {
        let circumference = rc(653.2);
        let t = table(&SIX_SPEED, 3.90, 7_000.0, circumference, 0.0);

        close(7_000.0 * 2.048 / 3.545, t[0].rpm_after_upshift, 6);
        in_range(t[0].rpm_after_upshift, 4_000.0, 4_100.0);

        in_range(t[4].rpm_after_upshift, 6_200.0, 6_300.0);

        assert!(t[t.len() - 1].rpm_after_upshift.is_nan());

        for step in &t {
            if !step.rpm_after_upshift.is_nan() {
                assert!(step.rpm_after_upshift < 7_000.0);
            }
        }
    }

    #[test]
    fn the_speed_is_unchanged_across_a_shift() {
        let circumference = rc(653.2);
        let t = table(&SIX_SPEED, 3.90, 7_000.0, circumference, 0.0);

        for i in 0..t.len() - 1 {
            let after = mph(t[i].rpm_after_upshift, SIX_SPEED[i + 1], 3.90, circumference);
            close(t[i].mph, after, 6);
        }
    }

    #[test]
    fn top_speed_is_the_tallest_gear_at_the_redline() {
        let circumference = rc(653.2);

        let top = geared_top_speed_mph(&SIX_SPEED, 3.90, 7_000.0, circumference);
        let t = table(&SIX_SPEED, 3.90, 7_000.0, circumference, 0.0);

        close(t[t.len() - 1].mph, top, 6);
        in_range(top, 160.0, 200.0);
    }

    #[test]
    fn a_cruising_speed_is_reported_in_every_gear_when_one_is_asked() {
        let circumference = rc(653.2);

        let without = table(&SIX_SPEED, 3.90, 7_000.0, circumference, 0.0);
        let with = table(&SIX_SPEED, 3.90, 7_000.0, circumference, 70.0);

        assert!(without.iter().all(|s| s.rpm_at_cruise.is_nan()));

        in_range(with[with.len() - 1].rpm_at_cruise, 2_700.0, 2_850.0);
        assert!(with[0].rpm_at_cruise > 7_000.0);

        for i in 1..with.len() {
            assert!(with[i].rpm_at_cruise < with[i - 1].rpm_at_cruise);
        }
    }

    #[test]
    fn gears_that_were_not_entered_are_not_invented() {
        let circumference = rc(653.2);

        assert_eq!(
            4,
            table(&[3.545, 2.048, 1.416, 1.059], 3.90, 7_000.0, circumference, 0.0).len()
        );
        assert!(table(&[], 3.90, 7_000.0, circumference, 0.0).is_empty());
        assert!(table(&SIX_SPEED, 0.0, 7_000.0, circumference, 0.0).is_empty());
        assert!(table(&SIX_SPEED, 3.90, 0.0, circumference, 0.0).is_empty());
    }

    #[test]
    fn nonsense_produces_nothing_rather_than_a_division_by_zero() {
        let circumference = rc(653.2);

        assert!(mph(7_000.0, 0.0, 3.90, circumference).is_nan());
        assert!(mph(7_000.0, 1.0, 0.0, circumference).is_nan());
        assert!(mph(7_000.0, 1.0, 3.90, 0.0).is_nan());
        assert!(rpm_at(0.0, 1.0, 3.90, circumference).is_nan());
        assert!(rc(0.0).is_nan());
    }

    #[test]
    fn the_chart_has_a_heading_and_a_row_per_gear() {
        let circumference = rc(653.2);

        let t = table(&SIX_SPEED, 3.90, 7_000.0, circumference, 70.0);

        let text = chart(&t, true);
        let lines: Vec<&str> = text.split('\n').collect();

        assert_eq!(t.len() + 1, lines.len());
        assert!(lines[0].contains("gear"));
        assert!(text.contains("3.545"));

        assert!(lines[lines.len() - 1].contains('\u{2014}'));
    }

    #[test]
    fn a_tyre_prints_as_its_sidewall() {
        assert_eq!("245/40R18", tyre("245/40R18").to_string());
    }
}
