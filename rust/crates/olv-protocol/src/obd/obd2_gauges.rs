//! Ported from `Obd2Gauges.cs`: gauges for an OBD2 vehicle, from the standard's own
//! definitions.
//!
//! There is no firmware INI and no equivalent of one, and for once none is needed:
//! SAE J1979 fixes what each parameter means and the range its encoding can hold, so
//! the dials come from the same document the decode does.
//!
//! The scales are the standard's. The warning and danger bands are not, because the
//! standard has none to give: it says what a value is and never what a safe one would
//! be. Those are conventions, and the gauge list says so rather than letting a
//! coloured arc pass for the car's own opinion.
//!
//! The exception is the malfunction lamp, where the standard's own word for the state
//! is "malfunction". A lit lamp being red is the car talking, not this.

use olv_core::ini::gauge_catalog::GaugeSpec;

use super::obd2_pids::Obd2Pid;

/// A gauge for every channel of every parameter the car reports.
pub fn for_pids(pids: &[Obd2Pid]) -> Vec<GaugeSpec> {
    let mut gauges = Vec::new();

    for pid in pids {
        for channel in &pid.channels {
            let mut gauge = GaugeSpec::new(&channel.name, &channel.name, &channel.name);
            gauge.units = channel.units.clone();

            // Named for the parameter as well, because a table of thirty of them is
            // easier to search by the number a service manual quotes than by a name
            // this chose.
            gauge.category = format!("OBD2 \u{b7} {pid}");
            gauge.low = channel.low;
            gauge.high = channel.high;
            gauge.low_danger = channel.low_danger;
            gauge.low_warning = channel.low_warning;
            gauge.high_warning = channel.high_warning;
            gauge.high_danger = channel.high_danger;
            gauge.value_digits = channel.digits;
            gauge.label_digits = if channel.digits > 1 { 1 } else { channel.digits };

            gauges.push(gauge);
        }
    }

    gauges
}

/// The handful worth putting on screen before anything is chosen.
///
/// A car reporting thirty parameters would otherwise open as thirty dials, most of
/// them diagnostic counters. These are the ones a driver watches, in the order a
/// dashboard puts them.
pub const FRONT_PAGE: [&str; 8] = [
    "RPM",
    "Speed",
    "Throttle Position",
    "Engine Load",
    "MAP",
    "Coolant",
    "IAT",
    "Battery",
];

#[cfg(test)]
mod tests {
    use super::*;
    use crate::obd::obd2_pids;

    #[test]
    fn every_reported_parameter_gets_a_gauge() {
        let gauges = for_pids(obd2_pids::all());
        // Every parameter has one channel except 0x01, which has two.
        assert_eq!(gauges.len(), obd2_pids::all().len() + 1);

        let rpm = gauges.iter().find(|g| g.name == "RPM").unwrap();
        assert_eq!(rpm.category, "OBD2 \u{b7} 010C");
        assert_eq!(rpm.high, obd2_pids::TACHOMETER_TOP);
        assert_eq!(rpm.label_digits, 0);

        let maf = gauges.iter().find(|g| g.name == "MAF").unwrap();
        assert_eq!(maf.value_digits, 2);
        assert_eq!(maf.label_digits, 1);
    }

    #[test]
    fn the_front_page_names_real_channels() {
        let gauges = for_pids(obd2_pids::all());
        for name in FRONT_PAGE {
            assert!(gauges.iter().any(|g| g.name == name), "{name}");
        }
    }
}
