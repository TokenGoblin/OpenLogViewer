//! Ported from `Obd2Codes.cs`: what a diagnostic trouble code means.
//!
//! Half of this is standardised and half of it is not, and the difference is the
//! whole point of the file. SAE J2012 fixes the meaning of the generic codes for
//! every vehicle sold: P0301 is a misfire on cylinder one on a Fiesta and on a
//! Ferrari. The manufacturer-specific ranges are the maker's to assign, so the same
//! five characters mean unrelated things on two cars in the same street.
//!
//! So a manufacturer code gets no description here rather than a plausible one.
//! A guess would be worse than the silence: somebody reads P1131 as "HO2S heater
//! circuit", which it is on a Ford and is not on the car in front of them, and goes
//! and buys a sensor. What this can honestly say about those is which
//! manufacturer's book to open.
//!
//! Even a generic description is the standard's definition and not a diagnosis.
//! "System too lean, bank 1" is the observation that made the light come on; the
//! cause is anything from a split hose to a tired pump, and the code cannot tell
//! which.

/// Which authority defines what a code means.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DtcAuthority {
    /// SAE J2012: the same meaning on every car ever built.
    Generic,
    /// The manufacturer's own. The number is theirs to assign, so the same digits
    /// mean one thing on a Ford and something unrelated on a Toyota.
    Manufacturer,
}

/// Whether the number belongs to the standard or to the maker.
///
/// The second character decides it, and the ranges are not symmetrical between the
/// four systems: P2 is generic while C2, B2 and U2 are the manufacturer's, and P3 is
/// split down the middle at P3400. Getting this wrong in the safe direction only
/// costs a description; getting it wrong the other way puts a confident SAE meaning
/// on a number that never had one.
pub fn authority(code: Option<&str>) -> DtcAuthority {
    let Some(code) = code.filter(|c| is_well_formed(Some(c))) else {
        return DtcAuthority::Manufacturer;
    };

    let bytes = code.as_bytes();
    let system = bytes[0].to_ascii_uppercase();
    let second = bytes[1];

    match system {
        b'P' => match second {
            b'0' | b'2' => DtcAuthority::Generic,

            // P3000 to P3399 are the maker's; P3400 up is the standard's: the
            // cylinder deactivation range.
            b'3' => {
                if number(code) >= 3400 {
                    DtcAuthority::Generic
                } else {
                    DtcAuthority::Manufacturer
                }
            }

            _ => DtcAuthority::Manufacturer,
        },

        // Chassis, body and network. Only the x0xxx range is the standard's; 1 and
        // 2 are the maker's, and 3 is reserved to the standard.
        b'C' | b'B' | b'U' => {
            if second == b'0' || second == b'3' {
                DtcAuthority::Generic
            } else {
                DtcAuthority::Manufacturer
            }
        }

        _ => DtcAuthority::Manufacturer,
    }
}

/// What the code covers, from its first letter.
pub fn system(code: Option<&str>) -> &'static str {
    let Some(code) = code.filter(|c| is_well_formed(Some(c))) else {
        return "";
    };

    match code.as_bytes()[0].to_ascii_uppercase() {
        b'P' => "Powertrain",
        b'C' => "Chassis",
        b'B' => "Body",
        _ => "Network",
    }
}

/// The standard's meaning, or empty where there is none to give.
///
/// Empty for a manufacturer code, and empty for a generic one this does not carry:
/// J2012 defines a few thousand and no list here will be all of them. Both are
/// reported as "no description" rather than as an error, because neither means the
/// code is invalid: the car set it, and the number itself is what a workshop manual
/// is looked up by.
pub fn describe(code: Option<&str>) -> String {
    if !is_well_formed(code) {
        return String::new();
    }
    if authority(code) == DtcAuthority::Manufacturer {
        return String::new();
    }

    let key = code.unwrap_or_default().to_ascii_uppercase();

    if let Some((_, text)) = DESCRIPTIONS.iter().find(|(k, _)| *k == key) {
        return (*text).to_string();
    }

    family(&key)
}

/// A letter and four hex digits, which is all a code ever is.
pub fn is_well_formed(code: Option<&str>) -> bool {
    let Some(code) = code else { return false };
    // Five UTF-16 units. Anything outside ASCII fails the character checks below
    // whatever its length, so the byte length is a safe stand-in.
    let bytes = code.as_bytes();
    if bytes.len() != 5 {
        return false;
    }

    if !b"PCBUpcbu".contains(&bytes[0]) {
        return false;
    }

    bytes[1..].iter().all(u8::is_ascii_hexdigit)
}

/// The four digits as a number, or -1 where any of them is a letter.
///
/// The digits are hex (a code can legitimately read P0A0F, and hybrids use that
/// range) but every family generated below is numbered in decimal, the way the
/// standard's tables are laid out. So anything with a letter in it falls out here
/// and is left to the explicit table.
fn number(code: &str) -> i32 {
    let mut value = 0;

    for &b in &code.as_bytes()[1..5] {
        if !b.is_ascii_digit() {
            return -1;
        }

        value = (value * 10) + i32::from(b - b'0');
    }

    value
}

/// Codes that come in runs, described by the rule that generates them.
///
/// Several hundred of the standard's codes are one description with a cylinder, a
/// bank or a sensor number in it, laid out at a fixed stride: misfires march one per
/// cylinder from P0301, injector circuits three per cylinder from P0261, oxygen
/// sensors six per sensor from P0130. Generated from the stride, so the arithmetic is
/// stated once and can be tested at both ends of each run.
fn family(code: &str) -> String {
    let n = number(code);
    if n < 0 {
        return String::new();
    }

    if !code.as_bytes()[0].eq_ignore_ascii_case(&b'P') {
        return String::new();
    }

    // Misfires: P0301 up, one per cylinder. P0300 is the random one and is in the
    // table, being a different sentence rather than the same one with a number in it.
    if (301..=312).contains(&n) {
        return format!("Cylinder {} misfire detected", n - 300);
    }

    // Injector circuit, open: one per cylinder from P0201.
    if (201..=212).contains(&n) {
        return format!("Injector circuit open \u{2014} cylinder {}", n - 200);
    }

    // Injector drive: three codes per cylinder from P0261: low, high, then the
    // contribution test that compares one cylinder's fuelling to the rest.
    if (261..=284).contains(&n) {
        let cylinder = ((n - 261) / 3) + 1;

        return match (n - 261) % 3 {
            0 => format!("Cylinder {cylinder} injector circuit low"),
            1 => format!("Cylinder {cylinder} injector circuit high"),
            _ => format!("Cylinder {cylinder} contribution or balance fault"),
        };
    }

    // Ignition coils: P0351 up, one per cylinder, lettered A to L in the standard's
    // own wording. The letter is the cylinder in firing order as the manufacturer
    // wired it, which is why both are worth saying.
    if (351..=362).contains(&n) {
        let cylinder = n - 350;
        let letter = char::from(b'A' + (cylinder - 1) as u8);

        return format!("Ignition coil {letter} primary or secondary circuit \u{2014} cylinder {cylinder}");
    }

    // Oxygen sensors: six codes per sensor, banks 1 and 2 twenty apart.
    if (130..=167).contains(&n) {
        return oxygen_sensor(n);
    }

    String::new()
}

/// One of the oxygen sensor codes, from where it sits in the run.
///
/// The layout is six codes per sensor and three sensors per bank, bank 1 starting at
/// P0130 and bank 2 at P0150, so the gap at P0148 and P0149 is real and has to fall
/// through rather than being counted over.
fn oxygen_sensor(n: i32) -> String {
    let bank = if n >= 150 { 2 } else { 1 };
    let offset = n - if bank == 1 { 130 } else { 150 };

    // Six per sensor, three sensors per bank: past that is the next bank's range or
    // the hole between them.
    if !(0..=17).contains(&offset) {
        return String::new();
    }

    let sensor = (offset / 6) + 1;

    let what = match offset % 6 {
        0 => "circuit malfunction",
        1 => "circuit low voltage",
        2 => "circuit high voltage",
        3 => "circuit slow response",
        4 => "circuit \u{2014} no activity detected",
        _ => "heater circuit malfunction",
    };

    format!("O2 sensor, bank {bank} sensor {sensor} \u{2014} {what}")
}

/// How many generic codes are named outright, for the about box and the tests.
pub fn named() -> usize {
    DESCRIPTIONS.len()
}

/// The generic codes worth naming, as SAE J2012 defines them.
///
/// Not every one of them: the standard runs to several thousand numbers, most of
/// which no one has ever seen set. These are the ones that actually turn a light on:
/// the sensors, the fuelling, the misfire and emissions families, the boost and knock
/// codes a tuner meets, and enough of the transmission and network ranges to
/// recognise where a fault lives.
///
/// Wording follows the standard rather than being improved on, so what is on screen
/// matches what a manual will say when it is looked up.
static DESCRIPTIONS: &[(&str, &str)] = &[
    ("P0010", "Camshaft position actuator A circuit — bank 1"),
    (
        "P0011",
        "Camshaft position — timing over-advanced or performance, bank 1",
    ),
    ("P0012", "Camshaft position — timing over-retarded, bank 1"),
    ("P0013", "Camshaft position actuator B circuit — bank 1"),
    (
        "P0014",
        "Camshaft position — timing over-advanced or performance, bank 1 exhaust",
    ),
    (
        "P0016",
        "Crankshaft and camshaft position correlation — bank 1 sensor A",
    ),
    (
        "P0017",
        "Crankshaft and camshaft position correlation — bank 1 sensor B",
    ),
    ("P0020", "Camshaft position actuator A circuit — bank 2"),
    (
        "P0021",
        "Camshaft position — timing over-advanced or performance, bank 2",
    ),
    ("P0022", "Camshaft position — timing over-retarded, bank 2"),
    ("P0087", "Fuel rail or system pressure too low"),
    ("P0088", "Fuel rail or system pressure too high"),
    ("P0089", "Fuel pressure regulator performance"),
    ("P0090", "Fuel pressure regulator control circuit"),
    ("P0100", "Mass or volume air flow circuit malfunction"),
    ("P0101", "Mass or volume air flow circuit range or performance"),
    ("P0102", "Mass or volume air flow circuit low input"),
    ("P0103", "Mass or volume air flow circuit high input"),
    ("P0105", "Manifold absolute pressure circuit malfunction"),
    ("P0106", "Manifold absolute pressure circuit range or performance"),
    ("P0107", "Manifold absolute pressure circuit low input"),
    ("P0108", "Manifold absolute pressure circuit high input"),
    ("P0110", "Intake air temperature circuit malfunction"),
    ("P0111", "Intake air temperature circuit range or performance"),
    ("P0112", "Intake air temperature circuit low input"),
    ("P0113", "Intake air temperature circuit high input"),
    ("P0115", "Engine coolant temperature circuit malfunction"),
    ("P0116", "Engine coolant temperature circuit range or performance"),
    ("P0117", "Engine coolant temperature circuit low input"),
    ("P0118", "Engine coolant temperature circuit high input"),
    ("P0119", "Engine coolant temperature circuit intermittent"),
    ("P0120", "Throttle or pedal position sensor A circuit malfunction"),
    (
        "P0121",
        "Throttle or pedal position sensor A circuit range or performance",
    ),
    ("P0122", "Throttle or pedal position sensor A circuit low input"),
    ("P0123", "Throttle or pedal position sensor A circuit high input"),
    ("P0125", "Coolant too cold for closed loop fuel control"),
    ("P0128", "Coolant thermostat — engine below its regulating temperature"),
    ("P0170", "Fuel trim malfunction — bank 1"),
    ("P0171", "System too lean — bank 1"),
    ("P0172", "System too rich — bank 1"),
    ("P0173", "Fuel trim malfunction — bank 2"),
    ("P0174", "System too lean — bank 2"),
    ("P0175", "System too rich — bank 2"),
    ("P0176", "Fuel composition sensor circuit malfunction"),
    ("P0182", "Fuel temperature sensor A circuit low input"),
    ("P0183", "Fuel temperature sensor A circuit high input"),
    ("P0190", "Fuel rail pressure sensor circuit malfunction"),
    ("P0191", "Fuel rail pressure sensor circuit range or performance"),
    ("P0192", "Fuel rail pressure sensor circuit low input"),
    ("P0193", "Fuel rail pressure sensor circuit high input"),
    ("P0200", "Injector circuit malfunction"),
    ("P0217", "Engine coolant over temperature"),
    ("P0219", "Engine overspeed"),
    (
        "P0221",
        "Throttle or pedal position sensor B circuit range or performance",
    ),
    ("P0222", "Throttle or pedal position sensor B circuit low input"),
    ("P0223", "Throttle or pedal position sensor B circuit high input"),
    ("P0230", "Fuel pump primary circuit malfunction"),
    ("P0231", "Fuel pump secondary circuit low"),
    ("P0232", "Fuel pump secondary circuit high"),
    ("P0233", "Fuel pump secondary circuit intermittent"),
    ("P0234", "Turbocharger or supercharger overboost"),
    ("P0235", "Turbocharger boost sensor A circuit malfunction"),
    ("P0236", "Turbocharger boost sensor A circuit range or performance"),
    ("P0237", "Turbocharger boost sensor A circuit low"),
    ("P0238", "Turbocharger boost sensor A circuit high"),
    ("P0243", "Turbocharger wastegate solenoid A malfunction"),
    ("P0245", "Turbocharger wastegate solenoid A low"),
    ("P0246", "Turbocharger wastegate solenoid A high"),
    ("P0299", "Turbocharger or supercharger underboost"),
    ("P0300", "Random or multiple cylinder misfire detected"),
    ("P0313", "Misfire detected with low fuel level"),
    ("P0316", "Misfire detected on startup — first 1,000 revolutions"),
    ("P0325", "Knock sensor 1 circuit — bank 1"),
    ("P0326", "Knock sensor 1 circuit range or performance — bank 1"),
    ("P0327", "Knock sensor 1 circuit low input — bank 1"),
    ("P0328", "Knock sensor 1 circuit high input — bank 1"),
    ("P0330", "Knock sensor 2 circuit — bank 2"),
    ("P0332", "Knock sensor 2 circuit low input — bank 2"),
    ("P0333", "Knock sensor 2 circuit high input — bank 2"),
    ("P0335", "Crankshaft position sensor A circuit malfunction"),
    ("P0336", "Crankshaft position sensor A circuit range or performance"),
    ("P0337", "Crankshaft position sensor A circuit low input"),
    ("P0338", "Crankshaft position sensor A circuit high input"),
    ("P0339", "Crankshaft position sensor A circuit intermittent"),
    ("P0340", "Camshaft position sensor A circuit — bank 1"),
    (
        "P0341",
        "Camshaft position sensor A circuit range or performance — bank 1",
    ),
    ("P0342", "Camshaft position sensor A circuit low input — bank 1"),
    ("P0343", "Camshaft position sensor A circuit high input — bank 1"),
    ("P0344", "Camshaft position sensor A circuit intermittent — bank 1"),
    ("P0345", "Camshaft position sensor A circuit — bank 2"),
    ("P0350", "Ignition coil primary or secondary circuit malfunction"),
    ("P0400", "Exhaust gas recirculation flow malfunction"),
    ("P0401", "Exhaust gas recirculation flow insufficient"),
    ("P0402", "Exhaust gas recirculation flow excessive"),
    ("P0403", "Exhaust gas recirculation circuit malfunction"),
    ("P0404", "Exhaust gas recirculation circuit range or performance"),
    ("P0405", "Exhaust gas recirculation sensor A circuit low"),
    ("P0406", "Exhaust gas recirculation sensor A circuit high"),
    ("P0410", "Secondary air injection system malfunction"),
    ("P0411", "Secondary air injection system — incorrect flow detected"),
    ("P0420", "Catalyst system efficiency below threshold — bank 1"),
    ("P0421", "Warm-up catalyst efficiency below threshold — bank 1"),
    ("P0430", "Catalyst system efficiency below threshold — bank 2"),
    ("P0431", "Warm-up catalyst efficiency below threshold — bank 2"),
    ("P0440", "Evaporative emission control system malfunction"),
    ("P0441", "Evaporative emission control system — incorrect purge flow"),
    ("P0442", "Evaporative emission control system — small leak detected"),
    ("P0443", "Evaporative emission control system purge valve circuit"),
    ("P0446", "Evaporative emission control system vent control circuit"),
    ("P0447", "Evaporative emission control system vent control circuit open"),
    (
        "P0448",
        "Evaporative emission control system vent control circuit shorted",
    ),
    ("P0455", "Evaporative emission control system — large leak detected"),
    (
        "P0456",
        "Evaporative emission control system — very small leak detected",
    ),
    (
        "P0457",
        "Evaporative emission control system leak — fuel cap loose or missing",
    ),
    ("P0500", "Vehicle speed sensor malfunction"),
    ("P0501", "Vehicle speed sensor range or performance"),
    ("P0503", "Vehicle speed sensor intermittent or erratic"),
    ("P0505", "Idle control system malfunction"),
    ("P0506", "Idle control system — RPM lower than expected"),
    ("P0507", "Idle control system — RPM higher than expected"),
    ("P0520", "Engine oil pressure sensor or switch circuit malfunction"),
    ("P0521", "Engine oil pressure sensor or switch range or performance"),
    ("P0522", "Engine oil pressure sensor or switch low voltage"),
    ("P0523", "Engine oil pressure sensor or switch high voltage"),
    ("P0532", "Air conditioning refrigerant pressure sensor circuit low"),
    ("P0533", "Air conditioning refrigerant pressure sensor circuit high"),
    ("P0560", "System voltage malfunction"),
    ("P0562", "System voltage low"),
    ("P0563", "System voltage high"),
    ("P0600", "Serial communication link malfunction"),
    ("P0601", "Internal control module memory checksum error"),
    ("P0602", "Control module programming error"),
    ("P0603", "Internal control module keep-alive memory error"),
    ("P0604", "Internal control module random access memory error"),
    ("P0605", "Internal control module read-only memory error"),
    ("P0606", "Control module processor fault"),
    ("P0620", "Generator control circuit malfunction"),
    ("P0622", "Generator field F control circuit malfunction"),
    ("P0627", "Fuel pump control circuit open"),
    ("P0628", "Fuel pump control circuit low"),
    ("P0629", "Fuel pump control circuit high"),
    ("P0700", "Transmission control system malfunction"),
    ("P0701", "Transmission control system range or performance"),
    ("P0703", "Brake switch B circuit malfunction"),
    ("P0705", "Transmission range sensor circuit malfunction"),
    ("P0706", "Transmission range sensor circuit range or performance"),
    ("P0710", "Transmission fluid temperature sensor circuit malfunction"),
    ("P0715", "Input or turbine speed sensor circuit malfunction"),
    ("P0720", "Output speed sensor circuit malfunction"),
    ("P0725", "Engine speed input circuit malfunction"),
    ("P0730", "Incorrect gear ratio"),
    ("P0740", "Torque converter clutch circuit malfunction"),
    ("P0741", "Torque converter clutch circuit — stuck off or performance"),
    ("P0743", "Torque converter clutch circuit electrical"),
    ("P0750", "Shift solenoid A malfunction"),
    ("P0755", "Shift solenoid B malfunction"),
    ("P0760", "Shift solenoid C malfunction"),
    ("P2187", "System too lean at idle — bank 1"),
    ("P2188", "System too rich at idle — bank 1"),
    ("P2189", "System too lean at idle — bank 2"),
    ("P2190", "System too rich at idle — bank 2"),
    ("P2195", "O2 sensor signal biased or stuck lean — bank 1 sensor 1"),
    ("P2196", "O2 sensor signal biased or stuck rich — bank 1 sensor 1"),
    ("P2197", "O2 sensor signal biased or stuck lean — bank 2 sensor 1"),
    ("P2198", "O2 sensor signal biased or stuck rich — bank 2 sensor 1"),
    ("P2101", "Throttle actuator control motor circuit range or performance"),
    ("P2111", "Throttle actuator control system — stuck open"),
    ("P2112", "Throttle actuator control system — stuck closed"),
    ("P2119", "Throttle actuator control throttle body range or performance"),
    ("P2135", "Throttle or pedal position sensor A and B correlation"),
    ("P2138", "Pedal position sensor D and E correlation"),
    ("U0100", "Lost communication with the engine control module"),
    ("U0101", "Lost communication with the transmission control module"),
    ("U0121", "Lost communication with the ABS control module"),
    ("U0140", "Lost communication with the body control module"),
    ("U0155", "Lost communication with the instrument panel cluster"),
    ("U0164", "Lost communication with the climate control module"),
    ("U0300", "Internal control module software incompatibility"),
    ("U0401", "Invalid data received from the engine control module"),
];

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_named_table_has_no_duplicates() {
        let mut keys: Vec<&str> = DESCRIPTIONS.iter().map(|(k, _)| *k).collect();
        keys.sort_unstable();
        keys.dedup();
        assert_eq!(keys.len(), named());
        assert_eq!(named(), 179);
    }

    #[test]
    fn authority_follows_the_second_character() {
        assert_eq!(authority(Some("P0301")), DtcAuthority::Generic);
        assert_eq!(authority(Some("p2187")), DtcAuthority::Generic);
        assert_eq!(authority(Some("P1131")), DtcAuthority::Manufacturer);
        assert_eq!(authority(Some("P3399")), DtcAuthority::Manufacturer);
        assert_eq!(authority(Some("P3400")), DtcAuthority::Generic);
        assert_eq!(authority(Some("P3A00")), DtcAuthority::Manufacturer);
        assert_eq!(authority(Some("U0100")), DtcAuthority::Generic);
        assert_eq!(authority(Some("U2100")), DtcAuthority::Manufacturer);
        assert_eq!(authority(Some("B3000")), DtcAuthority::Generic);
        assert_eq!(authority(Some("nonsense")), DtcAuthority::Manufacturer);
        assert_eq!(authority(None), DtcAuthority::Manufacturer);
    }

    #[test]
    fn the_runs_are_generated_from_their_stride() {
        assert_eq!(describe(Some("P0301")), "Cylinder 1 misfire detected");
        assert_eq!(describe(Some("P0312")), "Cylinder 12 misfire detected");
        assert_eq!(describe(Some("P0313")), "Misfire detected with low fuel level");
        assert_eq!(describe(Some("P0261")), "Cylinder 1 injector circuit low");
        assert_eq!(describe(Some("P0284")), "Cylinder 8 contribution or balance fault");
        assert_eq!(
            describe(Some("P0351")),
            "Ignition coil A primary or secondary circuit \u{2014} cylinder 1"
        );
        assert_eq!(
            describe(Some("P0130")),
            "O2 sensor, bank 1 sensor 1 \u{2014} circuit malfunction"
        );
        assert_eq!(
            describe(Some("P0167")),
            "O2 sensor, bank 2 sensor 3 \u{2014} heater circuit malfunction"
        );
        assert_eq!(describe(Some("P0148")), "");
        assert_eq!(describe(Some("P1131")), "");
        assert_eq!(describe(Some("P0A0F")), "");
    }

    #[test]
    fn well_formedness_is_a_letter_and_four_hex_digits() {
        assert!(is_well_formed(Some("P0A0F")));
        assert!(is_well_formed(Some("u0100")));
        assert!(!is_well_formed(Some("X0100")));
        assert!(!is_well_formed(Some("P010")));
        assert!(!is_well_formed(Some("P01G0")));
        assert!(!is_well_formed(None));
    }
}
