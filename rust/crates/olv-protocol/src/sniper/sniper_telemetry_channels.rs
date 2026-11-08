//! Ported from `SniperTelemetryChannels.cs`. Owned by package `w1-sniper`.
//!
//! The Sniper ECU's fixed 119-channel telemetry catalog, transcribed from the spec's
//! `telemetry_channels.csv` (read from the master table at `DAT_0059db14` in the decompiled
//! app).
//!
//! **What this catalog cannot do: turn a polled reply into a value.** The csv's own
//! `source_offset_or_pid` column says, for every one of the 119 rows, "runtime accessor
//! switch (val getter keyed by index)": the value for a channel is fetched by code, not read
//! from a fixed struct offset the catalog records. Spec section 5: "The exact cache offset
//! per channel index is not yet mapped" (UNCONFIRMED, needs a hardware capture). Until then
//! this catalog is names, units and ranges only. See `SniperSession::poll_telemetry_raw`,
//! which returns exactly the undecoded bytes this catalog cannot yet turn into channels.

/// One entry in the catalog. Name, units and declared range only; it does not say where a
/// channel's value lives in a polled reply.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SniperTelemetryChannel {
    /// 0-based position in the ECU's own master table (what a `.dm` gauge file's
    /// `ChannelID` is). Stable and firmware-defined, not invented here.
    pub index: i32,
    /// The ECU's own display name for the channel.
    pub name: &'static str,
    /// Empty where the channel is unitless or dimensionless.
    pub units: &'static str,
    /// The declared minimum, from the master table.
    pub min: f64,
    /// The declared maximum.
    pub max: f64,
    /// The raw flag word the catalog carries per entry. Bit 0 is documented (spec section 5:
    /// "flags(bit0=hidden default)"); the remaining bits are uninterpreted but recorded.
    pub default_visibility_flags: i32,
}

impl SniperTelemetryChannel {
    /// Whether Sniper.exe's own Data Monitor hides this channel by default (section 5, bit 0).
    pub fn hidden_by_default(&self) -> bool {
        self.default_visibility_flags & 1 != 0
    }
}

const fn ch(
    index: i32,
    name: &'static str,
    units: &'static str,
    min: f64,
    max: f64,
    default_visibility_flags: i32,
) -> SniperTelemetryChannel {
    SniperTelemetryChannel {
        index,
        name,
        units,
        min,
        max,
        default_visibility_flags,
    }
}

/// The whole catalog, in master-table order.
pub static ALL: [SniperTelemetryChannel; 119] = [
    ch(0, "Placeholder", "", 0.0, 1.0, 0),
    ch(1, "RTC", "sec", -1000000.0, 1000000.0, 0),
    ch(2, "RPM", "RPM", 0.0, 20000.0, 256),
    ch(3, "Inj PW", "msec", 0.0, 70.0, 256),
    ch(4, "Duty Cycle", "", 0.0, 100.0, 256),
    ch(5, "CL Comp", "", 0.0, 999.0, 0),
    ch(6, "Target AFR", "A/F", 2.0, 20.0, 256),
    ch(7, "AFR", "A/F", 2.0, 20.0, 256),
    ch(8, "Air Temp Enr", "", 0.0, 600.0, 256),
    ch(9, "Coolant Enr", "", 70.0, 200.0, 256),
    ch(10, "Coolant AFR Offset", "A/F", -5.0, 5.0, 256),
    ch(11, "Afterstart Enr", "", 75.0, 300.0, 256),
    ch(12, "Current Learn", "", -999.0, 999.0, 256),
    ch(13, "CL Status", "", 0.0, 1.0, 257),
    ch(14, "Learn Status", "", 0.0, 1.0, 257),
    ch(15, "Fuel Flow", "lb/hr", 0.0, 9999.9004, 256),
    ch(16, "MAP RoC", "kpa/sec x10", 0.0, 100.0, 256),
    ch(17, "TPS RoC", "", 0.0, 1000.0, 256),
    ch(18, "Estimated VE", "", 0.0, 999.0, 256),
    ch(19, "Ignition Timing", "", 0.0, 60.0, 256),
    ch(20, "Main Rev Limit", "", 0.0, 1.0, 257),
    ch(21, "Rev Limit #1", "", 0.0, 1.0, 257),
    ch(22, "Launch Retard", "", 0.0, 60.0, 256),
    ch(23, "IAC Position", "", 0.0, 100.0, 256),
    ch(24, "MAP", "kPa", 0.0, 999.0, 256),
    ch(25, "TPS", "", 0.0, 100.0, 256),
    ch(26, "MAT", "", -40.0, 999.0, 256),
    ch(27, "CTS", "", -40.0, 999.0, 256),
    ch(28, "Battery", "Volts", 0.0, 20.0, 256),
    ch(29, "Custom 1", "", -99999.0, 99999.0, 256),
    ch(30, "Custom 2", "", -99999.0, 99999.0, 256),
    ch(31, "Custom 3", "", -99999.0, 99999.0, 256),
    ch(32, "AC Kick", "", 0.0, 1.0, 257),
    ch(33, "Fan #1", "", 0.0, 1.0, 257),
    ch(34, "Fan #2", "", 0.0, 1.0, 257),
    ch(35, "#2 Fuel Pump", "", 0.0, 1.0, 257),
    ch(36, "AC Shutdown", "", 0.0, 1.0, 257),
    ch(37, "Sensor Warning", "", 0.0, 1.0, 257),
    ch(38, "Sensor Caution", "", 0.0, 1.0, 257),
    ch(39, "Base Fuel lb/hr", "lb/hr", 0.0, 9999.0, 256),
    ch(40, "Base Fuel VE", "", 0.0, 9999.0, 256),
    ch(41, "Base Timing", "", 0.0, 60.0, 256),
    ch(42, "Base Target AFR", "A/F", 2.0, 20.0, 256),
    ch(43, "Base Ign Dwell", "msec", 0.0, 10.0, 256),
    ch(44, "Vol Comp Ign Dwell", "msec", 0.0, 10.0, 256),
    ch(45, "Timing vs Air", "", -20.0, 20.0, 256),
    ch(46, "Timing vs Cool", "", -20.0, 20.0, 256),
    ch(47, "Status 1", "", -99999.0, 99999.0, 0),
    ch(48, "Status 2", "", -99999.0, 99999.0, 0),
    ch(49, "Status 3", "", -99999.0, 99999.0, 0),
    ch(50, "Status 4", "", -99999.0, 99999.0, 0),
    ch(51, "Status 5", "", -99999.0, 99999.0, 0),
    ch(52, "Status 6", "", -99999.0, 99999.0, 0),
    ch(53, "Status 7", "", -99999.0, 99999.0, 0),
    ch(54, "Status 8", "", -99999.0, 99999.0, 0),
    ch(55, "Inj Set#1 PPH", "lb/hr", 0.0, 5000.0, 0),
    ch(56, "Inj Set#2 PPH", "lb/hr", 0.0, 5000.0, 0),
    ch(57, "Inj Set#1 PW", "msec", 0.0, 999.0, 0),
    ch(58, "Inj Set#2 PW", "msec", 0.0, 999.0, 0),
    ch(59, "Boost PSIG", "psig", 0.0, 999.0, 256),
    ch(60, "Boost Time", "sec", 0.0, 99.0, 256),
    ch(61, "Target Boost", "psig", 0.0, 58.0, 256),
    ch(62, "Trans Brake", "", 0.0, 1.0, 257),
    ch(63, "Boost Solenoid Duty", "", -100.0, 100.0, 256),
    ch(64, "Boost Safety", "", 0.0, 1.0, 256),
    ch(65, "Boost Master Enbl", "", 0.0, 1.0, 257),
    ch(66, "Boost Fill Sol DC", "", 0.0, 100.0, 256),
    ch(67, "Boost Vent Sol DC", "", 0.0, 100.0, 256),
    ch(68, "N2O Stage 1", "", 0.0, 100.0, 256),
    ch(69, "N2O Enabled", "", 0.0, 1.0, 257),
    ch(70, "N2O Input #1", "", 0.0, 1.0, 257),
    ch(71, "N2O Lean Cutoff", "", 0.0, 1.0, 257),
    ch(72, "N2O Rich Cutoff", "", 0.0, 1.0, 257),
    ch(73, "N2O RPM Cutoff", "", 0.0, 1.0, 257),
    ch(74, "N2O MAP Cutoff", "", 0.0, 1.0, 257),
    ch(75, "N2O Dry Fuel #1", "lb/hr", 0.0, 600.0, 256),
    ch(76, "N2O Tmg Mod #1", "", 0.0, 60.0, 256),
    ch(77, "N2O Timer #1", "sec", 0.0, 999.0, 256),
    ch(78, "Diag #1", "", -10000.0, 10000.0, 0),
    ch(79, "Diag #2", "", -10000.0, 10000.0, 0),
    ch(80, "Diag #3", "", -10000.0, 10000.0, 0),
    ch(81, "Diag #4", "", -10000.0, 10000.0, 0),
    ch(82, "Diag #5", "", -10000.0, 10000.0, 0),
    ch(83, "Diag #6", "", -10000.0, 10000.0, 0),
    ch(84, "Diag #7", "", -10000.0, 10000.0, 0),
    ch(85, "Diag #8", "", -10000.0, 10000.0, 0),
    ch(86, "Diag #9", "", -10000.0, 10000.0, 0),
    ch(87, "Diag #10", "", -10000.0, 10000.0, 0),
    ch(88, "Diag #11", "", -10000.0, 10000.0, 0),
    ch(89, "Diag #12", "", -10000.0, 10000.0, 0),
    ch(90, "Diag #13", "", -10000.0, 10000.0, 0),
    ch(91, "Diag #14", "", -10000.0, 10000.0, 0),
    ch(92, "Diag #15", "", -10000.0, 10000.0, 0),
    ch(93, "Diag #16", "", -10000.0, 10000.0, 0),
    ch(94, "Diag #17", "", -10000.0, 10000.0, 0),
    ch(95, "Diag #18", "", -10000.0, 10000.0, 0),
    ch(96, "Diag #19", "", -10000.0, 10000.0, 0),
    ch(97, "Diag #20", "", -10000.0, 10000.0, 0),
    ch(98, "AT Launch Input", "", 0.0, 1.0, 257),
    ch(99, "AT 1D #1", "", 0.0, 100.0, 0),
    ch(100, "AT 1D #2", "", 0.0, 100.0, 0),
    ch(101, "AT 1D #3", "", 0.0, 100.0, 0),
    ch(102, "AT 1D #4", "", 0.0, 100.0, 0),
    ch(103, "AT 2D #1", "", 0.0, 100.0, 0),
    ch(104, "AT 2D #2", "", 0.0, 100.0, 0),
    ch(105, "TCC Lockup", "", 0.0, 1.0, 257),
    ch(106, "Gear", "", 0.0, 8.0, 256),
    ch(107, "Speed", "MPH", 0.0, 300.0, 256),
    ch(108, "Line Pressure", "", 0.0, 100.0, 256),
    ch(109, "Input Shaft Speed", "RPM", 0.0, 20000.0, 256),
    ch(110, "Accum Pressure", "", 0.0, 100.0, 256),
    ch(111, "TCC Duty Cycle", "", 0.0, 100.0, 256),
    ch(112, "Line Temp", "", -50.0, 325.0, 256),
    ch(113, "Trans Man US Input", "", 0.0, 1.0, 257),
    ch(114, "Trans Man DS Input", "", 0.0, 1.0, 257),
    ch(115, "Trans Auto/Man In", "", 0.0, 1.0, 257),
    ch(116, "Trans Range Pos", "", -2.0, 4.0, 256),
    ch(117, "Line Pressure PSI", "psi", 0.0, 500.0, 256),
    ch(118, "Trans Fan", "", 0.0, 1.0, 256),
];

/// Looks a channel up by its master-table index, or `None` if out of range.
pub fn by_index(index: i32) -> Option<&'static SniperTelemetryChannel> {
    usize::try_from(index).ok().and_then(|i| ALL.get(i))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn there_are_exactly_the_one_hundred_nineteen_channels_the_spec_describes() {
        assert_eq!(ALL.len(), 119);
    }

    #[test]
    fn every_entrys_index_matches_its_position_in_the_list() {
        for (i, c) in ALL.iter().enumerate() {
            assert_eq!(i as i32, c.index);
        }
    }

    /// Section 5's own "sample indices" list, checked against the full transcription.
    #[test]
    fn named_highlight_channels_match_the_specs_sample_indices() {
        let cases = [
            (2, "RPM"),
            (3, "Inj PW"),
            (4, "Duty Cycle"),
            (6, "Target AFR"),
            (7, "AFR"),
            (15, "Fuel Flow"),
            (18, "Estimated VE"),
            (19, "Ignition Timing"),
            (24, "MAP"),
            (25, "TPS"),
            (26, "MAT"),
            (27, "CTS"),
            (28, "Battery"),
            (107, "Speed"),
            (109, "Input Shaft Speed"),
        ];
        for (index, name) in cases {
            assert_eq!(by_index(index).map(|c| c.name), Some(name), "index {index}");
        }
    }

    #[test]
    fn by_index_returns_none_outside_the_catalog() {
        assert!(by_index(-1).is_none());
        assert!(by_index(119).is_none());
    }

    /// Section 5: "flags(bit0=hidden default)", spot-checked against a couple of rows.
    #[test]
    fn hidden_by_default_reads_bit_zero_of_the_flag_word() {
        // RPM: default flag=256 (0x100), bit 0 clear -> shown by default.
        assert!(!by_index(2).unwrap().hidden_by_default());
        // Main Rev Limit: default flag=257 (0x101), bit 0 set -> hidden by default.
        assert!(by_index(20).unwrap().hidden_by_default());
    }
}
