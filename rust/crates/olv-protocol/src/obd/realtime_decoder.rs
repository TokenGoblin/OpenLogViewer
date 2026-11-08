//! Ported from `RealtimeDecoder.cs`: turns a realtime block from an ECU into channel
//! values.
//!
//! Byte order is whatever the INI declares, because it differs by firmware and there is
//! no way to tell from the bytes: MegaSquirt runs on a Freescale S12 and is big-endian,
//! rusEFI runs on an ARM and is little. Read the wrong way round, a block does not fail:
//! every channel comes out a different number that is still a number.

use olv_core::base::math_expression::MathExpression;
use olv_core::ini::msq_ini::{RealtimeExpression, RealtimeField, RealtimeLayout, RealtimeType};
use olv_core::model::eq_ignore_case;

/// Decodes realtime blocks for one firmware layout.
pub struct RealtimeDecoder {
    layout: RealtimeLayout,
    fields: Vec<RealtimeField>,
    expressions: Vec<Option<MathExpression>>,
    declared: Vec<RealtimeExpression>,
    little_endian: bool,

    /// The tune's single-value settings, keyed case-insensitively.
    settings: Vec<(String, f64)>,

    names: Vec<String>,
    unresolved: Vec<String>,
}

impl RealtimeDecoder {
    /// Builds a decoder for one firmware layout.
    ///
    /// `tune_settings` supplies the tune's single-value settings, in the order the
    /// caller wants them offered to the expression parser. The firmware's derived
    /// channels are written in terms of the tune as well as the wire (duty cycle divides
    /// by `nCylinders`), so without them a large share of the channels cannot be
    /// computed at all.
    ///
    /// A key repeated in a different case replaces the earlier value, as the C#'s
    /// `Dictionary` built with `OrdinalIgnoreCase` would have thrown on it; the
    /// first key's spelling and position are kept.
    pub fn new(layout: RealtimeLayout, tune_settings: Option<&[(String, f64)]>) -> Self {
        let fields = layout.fields.clone();
        let declared = layout.expressions.clone();
        let little_endian = layout.little_endian;

        let mut settings: Vec<(String, f64)> = Vec::new();
        for (key, value) in tune_settings.unwrap_or_default() {
            match settings.iter_mut().find(|(k, _)| eq_ignore_case(k, key)) {
                Some(existing) => existing.1 = *value,
                None => settings.push((key.clone(), *value)),
            }
        }

        // Resolved once. An expression naming something neither the firmware nor the
        // tune publishes yields nothing and is reported rather than retried on every
        // sample.
        let mut known: Vec<String> = fields.iter().map(|f| f.name.clone()).collect();
        known.extend(settings.iter().map(|(k, _)| k.clone()));

        let mut expressions: Vec<Option<MathExpression>> = Vec::with_capacity(declared.len());
        let mut unresolved = Vec::new();

        for d in &declared {
            match MathExpression::try_parse(&d.expression, known.iter()) {
                Ok(parsed) => {
                    expressions.push(Some(parsed));
                    known.push(d.name.clone());
                }
                Err(_) => {
                    expressions.push(None);
                    unresolved.push(d.name.clone());
                }
            }
        }

        let names = fields
            .iter()
            .map(|f| f.name.clone())
            .chain(
                declared
                    .iter()
                    .zip(&expressions)
                    .filter(|(_, e)| e.is_some())
                    .map(|(d, _)| d.name.clone()),
            )
            .collect();

        Self {
            layout,
            fields,
            expressions,
            declared,
            little_endian,
            settings,
            names,
            unresolved,
        }
    }

    pub fn layout(&self) -> &RealtimeLayout {
        &self.layout
    }

    /// Channel names, aligned with what [`RealtimeDecoder::decode`] returns.
    pub fn names(&self) -> &[String] {
        &self.names
    }

    /// Derived channels that could not be resolved against this firmware.
    pub fn unresolved_expressions(&self) -> &[String] {
        &self.unresolved
    }

    /// The derived channels that did resolve, in declaration order.
    pub fn resolved(&self) -> impl Iterator<Item = &RealtimeExpression> {
        self.declared
            .iter()
            .zip(&self.expressions)
            .filter(|(_, e)| e.is_some())
            .map(|(d, _)| d)
    }

    /// Units for each entry of [`RealtimeDecoder::names`].
    pub fn units(&self) -> Vec<String> {
        self.fields
            .iter()
            .map(|f| f.units.clone())
            .chain(self.resolved().map(|e| e.units.clone()))
            .collect()
    }

    /// Display precision for each entry of [`RealtimeDecoder::names`].
    pub fn digits(&self) -> Vec<i32> {
        self.fields
            .iter()
            .map(|f| f.digits)
            .chain(self.resolved().map(|_| 2))
            .collect()
    }

    /// Decodes one block. A block shorter than the layout expects yields NaN for the
    /// fields that fall off the end rather than failing: a truncated read on a flaky
    /// link should cost those channels, not the session.
    pub fn decode(&self, block: &[u8]) -> Vec<f64> {
        let mut values = vec![0.0; self.names.len()];

        for (i, field) in self.fields.iter().enumerate() {
            values[i] = read(block, field, self.little_endian);
        }

        for (at, expression) in (self.fields.len()..).zip(self.expressions.iter().flatten()) {
            let arguments: Vec<f64> = expression
                .references()
                .iter()
                .map(|r| self.value(r, &values, at))
                .collect();

            values[at] = expression.evaluate(&arguments);
        }

        values
    }

    /// A referenced value: from the block and the channels computed so far, or failing
    /// that from the tune, which does not change between samples.
    ///
    /// Searched backwards because an INI may define a name twice (three do in the MS3
    /// firmware) and the later definition is the one in force, as it would be for any
    /// later assignment.
    fn value(&self, name: &str, values: &[f64], limit: usize) -> f64 {
        for i in (0..limit).rev() {
            if eq_ignore_case(&self.names[i], name) {
                return values[i];
            }
        }

        self.settings
            .iter()
            .find(|(k, _)| eq_ignore_case(k, name))
            .map_or(f64::NAN, |(_, v)| *v)
    }
}

fn read(block: &[u8], field: &RealtimeField, little: bool) -> f64 {
    // `field.Offset + field.Size > block.Length`, in the C#'s int arithmetic. A
    // negative offset would throw there on the slice that follows; it is NaN here.
    let size = field.size() as i64;
    let offset = i64::from(field.offset);
    if offset + size > block.len() as i64 || offset < 0 {
        return f64::NAN;
    }
    let at = &block[offset as usize..];

    let raw: f64 = match field.type_ {
        RealtimeType::U08 => f64::from(at[0]),
        RealtimeType::S08 => f64::from(at[0] as i8),
        RealtimeType::U16 => {
            let b = [at[0], at[1]];
            f64::from(if little {
                u16::from_le_bytes(b)
            } else {
                u16::from_be_bytes(b)
            })
        }
        RealtimeType::S16 => {
            let b = [at[0], at[1]];
            f64::from(if little {
                i16::from_le_bytes(b)
            } else {
                i16::from_be_bytes(b)
            })
        }
        RealtimeType::U32 => {
            let b = [at[0], at[1], at[2], at[3]];
            f64::from(if little {
                u32::from_le_bytes(b)
            } else {
                u32::from_be_bytes(b)
            })
        }
        RealtimeType::S32 => {
            let b = [at[0], at[1], at[2], at[3]];
            f64::from(if little {
                i32::from_le_bytes(b)
            } else {
                i32::from_be_bytes(b)
            })
        }
        RealtimeType::F32 => {
            let b = [at[0], at[1], at[2], at[3]];
            f64::from(if little {
                f32::from_le_bytes(b)
            } else {
                f32::from_be_bytes(b)
            })
        }
    };

    if !field.is_bit_field() {
        return (raw + field.transform) * field.scale;
    }

    // Packed flags: the INI names each run of bits separately, so the value is the run
    // shifted down to zero rather than the byte it came from.
    //
    // C# shifts mask their count to six bits, which `wrapping_shl`/`wrapping_shr` do.
    let width = field.bit_high - field.bit_low + 1;
    let mask = 1i64.wrapping_shl(width as u32).wrapping_sub(1);

    ((raw as i64).wrapping_shr(field.bit_low as u32) & mask) as f64
}

#[cfg(test)]
mod tests {
    use super::*;
    use olv_core::ini::msq_ini::{self, DatalogEntry, SymbolSet};

    /// An INI shaped like the real thing, conditionals and all.
    const INI: &str = "[MegaTune]
signature = \"test\"

[OutputChannels]
ochBlockSize     = 16
#if CAN_COMMANDS
ochGetCommand    = \"r\\$tsCanId\\x07%2o%2c\"
#else
ochGetCommand       = \"A\"
#endif

seconds          = scalar, U16,    0, \"s\",   1.000, 0.0
rpm              = scalar, U16,    2, \"RPM\", 1.000, 0.0
map              = scalar, S16,    4, \"kPa\", 0.100, 0.0
#if CELSIUS
coolant          = scalar, S16,    6, \"\u{b0}C\",  0.100, -40.0
#else
coolant          = scalar, S16,    6, \"\u{b0}F\",  0.100, -40.0
#endif
firing1          = bits,   U08,    8, [0:0]
mode             = bits,   U08,    8, [1:3]
secl             = { seconds % 256 }, \"s\"
halfRpm          = { rpm / 2 }, \"RPM\"

[Datalog]
entry = rpm,     \"RPM\",       int,   \"%d\"
entry = map,     \"MAP\",       float, \"%.1f\"
entry = coolant, \"CLT\",       float, \"%.2f\"
entry = halfRpm, \"Half RPM\",  float, \"%.1f\"
";

    fn layout(symbols: &[&str]) -> RealtimeLayout {
        let set = SymbolSet::ignore_case(symbols.iter().copied());
        msq_ini::read_output_channels(INI, Some(&set))
    }

    fn block() -> [u8; 16] {
        let mut block = [0u8; 16];
        block[0] = 0x00;
        block[1] = 0x0A; // seconds = 10
        block[2] = 0x0B;
        block[3] = 0xB8; // rpm = 3000
        block[4] = 0x03;
        block[5] = 0xE8; // map raw 1000 -> 100.0 kPa
        block[6] = 0x08;
        block[7] = 0x34; // coolant raw 2100 -> (2100 - 40) * 0.1 = 206.0
        block[8] = 0b0000_1011; // firing1 = 1, mode = bits 1..3 = 0b101 = 5
        block
    }

    fn field<'a>(layout: &'a RealtimeLayout, name: &str) -> &'a RealtimeField {
        let mut hits = layout.fields.iter().filter(|f| f.name == name);
        let one = hits.next().expect("field present");
        assert!(hits.next().is_none(), "{name} appears once");
        one
    }

    fn value(decoder: &RealtimeDecoder, values: &[f64], name: &str) -> f64 {
        // Backwards, matching the decoder: a later definition of a name wins.
        for i in (0..decoder.names().len()).rev() {
            if eq_ignore_case(&decoder.names()[i], name) {
                return values[i];
            }
        }
        f64::NAN
    }

    fn close(a: f64, b: f64) -> bool {
        (a - b).abs() < 1e-4
    }

    // ----- layout -----------------------------------------------------------

    #[test]
    fn the_block_size_and_command_are_read() {
        let layout = layout(&["CAN_COMMANDS"]);

        assert_eq!(layout.block_size, 16);
        assert!(!layout.uses_simple_command());
        assert!(layout.get_command.starts_with('r'));
    }

    #[test]
    fn the_other_branch_of_a_conditional_is_taken() {
        // Without CAN_COMMANDS the plain serial command is the live one.
        let layout = layout(&[]);

        assert!(layout.uses_simple_command());
        assert_eq!(layout.get_command, "A");
    }

    #[test]
    fn only_one_branch_of_a_conditional_contributes_fields() {
        // Both branches define "coolant". Taking both would leave two channels of the
        // same name disagreeing about units.
        let layout = layout(&["CAN_COMMANDS"]);

        assert_eq!(field(&layout, "coolant").units, "\u{b0}F");
    }

    #[test]
    fn the_selected_branch_follows_the_symbols() {
        let layout = layout(&["CELSIUS"]);

        assert_eq!(field(&layout, "coolant").units, "\u{b0}C");
    }

    #[test]
    fn scalars_carry_their_offset_scale_and_transform() {
        let layout = layout(&[]);
        let map = field(&layout, "map");

        assert_eq!(map.type_, RealtimeType::S16);
        assert_eq!(map.offset, 4);
        assert!((map.scale - 0.1).abs() < 1e-6);
        assert_eq!(map.units, "kPa");
        assert_eq!(map.digits, 1); // inferred from the scale
    }

    #[test]
    fn bit_fields_carry_their_bit_range() {
        let layout = layout(&[]);
        let mode = field(&layout, "mode");

        assert!(mode.is_bit_field());
        assert_eq!(mode.offset, 8);
        assert_eq!(mode.bit_low, 1);
        assert_eq!(mode.bit_high, 3);
    }

    #[test]
    fn expressions_are_collected_separately() {
        let layout = layout(&[]);

        assert!(
            layout
                .expressions
                .iter()
                .any(|e| e.name == "secl" && e.expression == "seconds % 256")
        );
        assert!(layout.expressions.iter().any(|e| e.name == "halfRpm"));
        assert!(!layout.fields.iter().any(|f| f.name == "secl"));
    }

    #[test]
    fn nothing_is_silently_skipped() {
        assert!(layout(&[]).skipped.is_empty());
    }

    #[test]
    fn a_degree_sign_survives_a_latin1_ini() {
        // Firmware INIs are ISO-8859-1 and say nothing about it. Read as UTF-8 they
        // mostly work, because almost everything in them is ASCII, and then the one byte
        // that matters in a units string decodes to a replacement character and every
        // temperature reads "?F".
        let mut latin1: Vec<u8> = b"[OutputChannels]\nochBlockSize = 4\ncoolant = scalar, S16, 0, \"".to_vec();
        latin1.extend_from_slice(&[0xB0, b'F']);
        latin1.extend_from_slice(b"\", 0.1, 0.0\n");

        let layout = msq_ini::read_output_channels(&olv_core::model::tuning_text::decode(&latin1), None);

        assert_eq!(layout.fields.len(), 1);
        assert_eq!(layout.fields[0].units, "\u{b0}F");
    }

    #[test]
    fn a_utf8_file_is_still_read_as_utf8() {
        // Only a file that is not valid UTF-8 falls back, so a modern INI keeps whatever
        // it says.
        let utf8 = "[OutputChannels]\nochBlockSize = 4\ncoolant = scalar, S16, 0, \"\u{b0}C\", 0.1, 0.0\n";

        let layout = msq_ini::read_output_channels(&olv_core::model::tuning_text::decode(utf8.as_bytes()), None);

        assert_eq!(layout.fields.len(), 1);
        assert_eq!(layout.fields[0].units, "\u{b0}C");
    }

    #[test]
    fn a_byte_order_mark_does_not_lead_the_first_line() {
        let mut with_bom = vec![0xEF, 0xBB, 0xBF];
        with_bom.extend_from_slice(b"[OutputChannels]\nochBlockSize = 64\n");

        assert_eq!(
            msq_ini::read_output_channels(&olv_core::model::tuning_text::decode(&with_bom), None).block_size,
            64
        );
    }

    // ----- decoding ---------------------------------------------------------

    #[test]
    fn scalars_decode_big_endian_with_scale_and_transform() {
        let decoder = RealtimeDecoder::new(layout(&[]), None);
        let values = decoder.decode(&block());

        assert!(close(value(&decoder, &values, "rpm"), 3000.0));
        assert!(close(value(&decoder, &values, "map"), 100.0));
        // The translate is added before the scale, not after: a Speeduino's coolant axis
        // only comes out in round Celsius numbers that way, and a rusEFI fuel trim of 1.0
        // only reads as nought per cent that way.
        assert!(close(value(&decoder, &values, "coolant"), 206.0));
    }

    #[test]
    fn bit_fields_decode_as_their_own_value() {
        // 0b0000_1011: bit 0 is 1, bits 1..3 are 0b101 = 5.
        let decoder = RealtimeDecoder::new(layout(&[]), None);
        let values = decoder.decode(&block());

        assert!(close(value(&decoder, &values, "firing1"), 1.0));
        assert!(close(value(&decoder, &values, "mode"), 5.0));
    }

    #[test]
    fn expressions_are_evaluated_from_the_decoded_values() {
        let decoder = RealtimeDecoder::new(layout(&[]), None);
        let values = decoder.decode(&block());

        assert!(close(value(&decoder, &values, "secl"), 10.0));
        assert!(close(value(&decoder, &values, "halfRpm"), 1500.0));
    }

    #[test]
    fn a_short_block_costs_only_the_fields_that_fall_off_the_end() {
        // A truncated read on a flaky link should not lose the whole sample.
        let decoder = RealtimeDecoder::new(layout(&[]), None);
        let values = decoder.decode(&block()[..4]);

        assert!(close(value(&decoder, &values, "rpm"), 3000.0));
        assert!(value(&decoder, &values, "coolant").is_nan());
    }

    #[test]
    fn an_expression_naming_something_absent_is_reported_not_thrown() {
        let ini = "[OutputChannels]\nochBlockSize = 4\n\
                   rpm     = scalar, U16, 0, \"RPM\", 1.0, 0.0\n\
                   broken  = { nCylinders * 2 }, \"\"\n";

        let decoder = RealtimeDecoder::new(msq_ini::read_output_channels(ini, None), None);

        assert_eq!(decoder.unresolved_expressions(), ["broken"]);
        assert!(decoder.names().iter().any(|n| n == "rpm"));
        assert!(!decoder.names().iter().any(|n| n == "broken"));
    }

    #[test]
    fn the_tune_supplies_what_the_wire_does_not() {
        // Firmware derives channels from tune settings as well as live values; without
        // them a large share cannot be computed at all.
        let ini = "[OutputChannels]\nochBlockSize = 4\n\
                   rpm      = scalar, U16, 0, \"RPM\", 1.0, 0.0\n\
                   perCyl   = { rpm / nCylinders }, \"RPM\"\n";

        let settings = vec![("nCylinders".to_string(), 6.0)];
        let decoder = RealtimeDecoder::new(msq_ini::read_output_channels(ini, None), Some(&settings));

        assert!(decoder.unresolved_expressions().is_empty());

        let values = decoder.decode(&[0x0B, 0xB8, 0, 0]);
        assert!(close(value(&decoder, &values, "perCyl"), 500.0));
    }

    // ----- datalog labels ---------------------------------------------------

    fn entry<'a>(entries: &'a [DatalogEntry], channel: &str) -> &'a DatalogEntry {
        let mut hits = entries.iter().filter(|e| e.channel == channel);
        let one = hits.next().expect("entry present");
        assert!(hits.next().is_none());
        one
    }

    #[test]
    fn datalog_entries_map_internal_names_to_log_labels() {
        // This is what lets a preset or a filter written against a recorded log work
        // unchanged against a live connection.
        let entries = msq_ini::read_datalog(INI, None);

        assert_eq!(entry(&entries, "rpm").label, "RPM");
        assert_eq!(entry(&entries, "coolant").label, "CLT");
        assert_eq!(entry(&entries, "halfRpm").label, "Half RPM");
    }

    #[test]
    fn datalog_precision_comes_from_the_format() {
        let entries = msq_ini::read_datalog(INI, None);

        assert_eq!(entry(&entries, "rpm").digits, 0);
        assert_eq!(entry(&entries, "map").digits, 1);
        assert_eq!(entry(&entries, "coolant").digits, 2);
    }

    // ----- channels that are only another channel renamed --------------------

    /// Speeduino's own idiom, verbatim from 202501.7: the throttle position is published
    /// as `tps`, aliased to `throttle`, and it is `throttle` that the front-page gauge
    /// names while the datalog records `tps`. Pairing them by name alone lost the gauge
    /// entirely.
    const ALIASES: &str = "[OutputChannels]
ochBlockSize = 4
ochGetCommand = \"r\\x00\\x30%2o%2c\"
rpm      = scalar, U16,  0, \"RPM\", 1.000, 0.000
tps      = scalar, U08,  2, \"%\",   0.500, 0.000
throttle         = { tps }, \"%\"
engineSpeed      = { rpm }
doubleThrottle   = { tps * 2 }
revolutionTime   = { rpm ? ( 60000.0 / rpm) : 0 }
";

    #[test]
    fn a_channel_that_is_another_channel_renamed_is_recognised() {
        let aliases = msq_ini::read_aliases(ALIASES, None);

        assert_eq!(aliases.get("throttle").unwrap(), "tps");
        assert_eq!(aliases.get("engineSpeed").unwrap(), "rpm");
    }

    #[test]
    fn a_channel_that_computes_something_is_not_an_alias() {
        // Following one of these as though it were would put the wrong number on a dial,
        // which is worse than showing no dial at all.
        let aliases = msq_ini::read_aliases(ALIASES, None);

        assert!(!aliases.contains_key("doubleThrottle"));
        assert!(!aliases.contains_key("revolutionTime"));
    }
}
