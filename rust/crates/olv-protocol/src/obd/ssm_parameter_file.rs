//! Ported from `SsmParameterFile.cs`: the list of SSM addresses to read, which you supply.
//!
//! The protocol is here; the addresses are not, and that is deliberate rather than
//! unfinished. What lives at which address is not something this can discover: the two
//! that are known were confirmed by reading them against the same values over OBD2, and
//! that method only works for parameters OBD2 already has, which are precisely the ones
//! not worth reaching SSM for. Knock correction and the learnt fuelling trims have nothing
//! to check them against.
//!
//! The published maps that do have them belong to other projects under licences this
//! cannot take from: RomRaider is GPL-2.0 against this project's MIT, and the
//! widely-copied definition repository declares no licence at all, which grants nothing
//! to anybody. So the file is yours to fill in, from whatever source you judge appropriate
//! for your own vehicle, the same arrangement as the ECU definition files this
//! application already expects you to provide.
//!
//! It also means this works on any Subaru rather than the one it was written against.
//!
//! `System.Text.Json` reads the file in the C#; here `serde_json` does, over the same
//! lenient syntax (comments skipped, trailing commas allowed), with property names matched
//! case-insensitively and any wrongly-typed value refusing the whole file, as it did there.

use std::path::{Path, PathBuf};

use olv_core::base::json_settings_file::make_lenient_json_strict;
use olv_core::model::eq_ignore_case;
use olv_core::{Error, Result};
use serde_json::{Map, Value};

use super::ssm::SsmParameter;

/// What the file is called inside the definitions folder.
pub const NAME: &str = "ssm-parameters.json";

/// Reads a parameter list, keeping the entries that make sense.
///
/// A bad entry is dropped rather than failing the file. Somebody filling this in by hand
/// against a forum post will get one wrong, and losing every parameter because the fourth
/// has a typo would be a poor trade: the ones that survive are reported, and the count is
/// what tells you something went missing.
///
/// A `null` entry in the list is skipped here; the C# would throw a
/// `NullReferenceException` on it.
pub fn read(json: Option<&str>) -> Vec<SsmParameter> {
    let Some(json) = json else { return Vec::new() };
    if json.chars().all(char::is_whitespace) {
        return Vec::new();
    }

    let Some(stored) = parse_file(json) else {
        return Vec::new();
    };
    if stored.is_empty() {
        return Vec::new();
    }

    let mut parameters: Vec<SsmParameter> = Vec::with_capacity(stored.len());

    for entry in &stored {
        let Some(name) = entry.name.as_deref().filter(|n| !n.is_empty()) else {
            continue;
        };
        let Some(address) = parse_address(entry.address.as_deref()) else {
            continue;
        };

        let parameter = SsmParameter {
            name: name.trim().to_string(),
            address,
            bytes: entry.bytes.unwrap_or(1),
            units: entry.units.as_deref().map_or(String::new(), |u| u.trim().to_string()),
            scale: entry.scale.unwrap_or(1.0),
            offset: entry.offset.unwrap_or(0.0),
            digits: entry.digits.unwrap_or(0),
            low: entry.low.unwrap_or(0.0),
            high: entry.high.unwrap_or(255.0),
            enabled: entry.enabled.unwrap_or(true),
        };

        // A duplicate name would give two channels the same column, and every preset and
        // filter matching on it would find whichever came first.
        if !parameter.is_usable() || parameters.iter().any(|p| eq_ignore_case(&p.name, &parameter.name)) {
            continue;
        }

        parameters.push(parameter);
    }

    parameters
}

/// Only the ones switched on, which is what a session actually reads.
///
/// The distinction earns its keep because one address per request makes the list a budget.
/// A file can hold every parameter a car offers (a hundred and sixty of them on this
/// protocol) while a dozen are switched on, and changing what you are watching is then a
/// matter of moving a flag rather than finding an address and its scaling again.
pub fn enabled(json: Option<&str>) -> Vec<SsmParameter> {
    read(json).into_iter().filter(|p| p.enabled).collect()
}

/// An address written the way people write them.
///
/// Hex, because every published SSM address is hex and nobody writes 14 when they mean
/// 0x0E. Accepted with or without the prefix; a bare decimal would be ambiguous with the
/// same digits in hex, so it is not offered.
pub fn parse_address(text: Option<&str>) -> Option<i32> {
    let text = text?;
    if text.chars().all(char::is_whitespace) {
        return None;
    }

    let mut trimmed = text.trim();

    if trimmed.len() >= 2 && trimmed.is_char_boundary(2) && trimmed[..2].eq_ignore_ascii_case("0x") {
        trimmed = &trimmed[2..];
    }

    // `int.TryParse(.., NumberStyles.HexNumber, InvariantCulture)`: optional leading and
    // trailing white space (tab to carriage return, and space), then hex digits only. The
    // digits are read as a 32-bit pattern, so eight of them can come out negative.
    let is_white = |c: char| matches!(c, '\t'..='\r' | ' ');
    let digits = trimmed.trim_start_matches(is_white).trim_end_matches(is_white);

    if digits.is_empty() || !digits.chars().all(|c| c.is_ascii_hexdigit()) {
        return None;
    }

    let mut value: u64 = 0;
    for c in digits.chars() {
        value = (value << 4) | u64::from(c.to_digit(16)?);
        if value > 0xFFFF_FFFF {
            return None;
        }
    }

    let value = value as u32 as i32;

    (0..=0xFF_FFFF).contains(&value).then_some(value)
}

/// A starting file, with the two addresses that were actually confirmed and nothing that
/// was not.
///
/// Written out the first time it is wanted so there is something to edit rather than a
/// format to guess at. The two entries are the ones proved on a running car by reading
/// them against OBD2; everything beyond that is left commented in the notes, because a
/// plausible-looking address that turns out to be something else is worse than an empty
/// file.
///
/// Line endings are `\n`. The C# raw string literal takes whatever the source file was
/// checked out with, so on Windows it is `\r\n`.
pub fn template() -> &'static str {
    TEMPLATE
}

const TEMPLATE: &str = r#"{
  "version": 1,

  "_notes": [
    "Addresses this application reads over SSM, which is Subaru's own",
    "protocol and reaches what the ECU has learnt rather than what it is",
    "measuring — knock correction, fine knock learning, IAM, fuelling",
    "trims. None of that is in OBD2 at any speed.",
    "",
    "The two below were confirmed on a running 2014 Crosstrek by reading",
    "them against the same values over OBD2. They are here as worked",
    "examples of the format, not because they are the interesting ones.",
    "",
    "The interesting addresses are not shipped. What lives where is not",
    "something this can work out — the confirmation method only works for",
    "values OBD2 already has, and the published maps belong to projects",
    "under licences this cannot take from. Fill them in yourself, from a",
    "source you judge right for your own car.",
    "",
    "address : hex, with or without 0x",
    "bytes   : how many consecutive bytes, most significant first",
    "scale   : multiplied by the raw value",
    "offset  : added afterwards, so a temperature is raw minus 40",
    "",
    "Reading is one address per request, about 146 ms each, so keep the",
    "list to what you actually want to watch. Eight is roughly 0.85 Hz."
  ],

  "parameters": [
    {
      "name": "Engine Speed",
      "address": "0x00000E",
      "bytes": 2,
      "units": "rpm",
      "scale": 0.25,
      "digits": 0,
      "low": 0,
      "high": 8000
    },
    {
      "name": "Coolant",
      "address": "0x000008",
      "bytes": 1,
      "units": "°C",
      "scale": 1,
      "offset": -40,
      "digits": 0,
      "low": -40,
      "high": 215
    }
  ]
}
"#;

/// The parameter list from the definitions folder, writing the template out the first time
/// so there is something to edit rather than a format to guess at.
///
/// A missing or unreadable file is no parameters rather than a failure. This runs on the
/// way to connecting, and a read-only folder should cost the SSM session and nothing else.
pub fn read_from(definitions_folder: &str) -> Result<Vec<SsmParameter>> {
    if definitions_folder.chars().all(char::is_whitespace) {
        return Err(Error::Argument(
            "The value cannot be an empty string or composed entirely of whitespace. \
             (Parameter 'definitionsFolder')"
                .to_string(),
        ));
    }

    let path = Path::new(definitions_folder).join(NAME);

    let attempt = || -> std::io::Result<Vec<SsmParameter>> {
        if !path.is_file() {
            std::fs::create_dir_all(definitions_folder)?;
            std::fs::write(&path, TEMPLATE)?;
        }

        // `File.ReadAllText`: a byte-order mark is dropped, invalid UTF-8 is replaced.
        let bytes = std::fs::read(&path)?;
        let text = String::from_utf8_lossy(&bytes);
        let text = text.strip_prefix('\u{feff}').unwrap_or(&text);

        Ok(enabled(Some(text)))
    };

    Ok(attempt().unwrap_or_default())
}

/// Where the file lives, for pointing somebody at it.
pub fn path_in(definitions_folder: &str) -> PathBuf {
    Path::new(definitions_folder).join(NAME)
}

// ----- the file as stored ------------------------------------------------------------

#[derive(Default)]
struct StoredParameter {
    name: Option<String>,
    address: Option<String>,
    bytes: Option<i32>,
    units: Option<String>,
    scale: Option<f64>,
    offset: Option<f64>,
    digits: Option<i32>,
    low: Option<f64>,
    high: Option<f64>,
    enabled: Option<bool>,
}

/// The entries of the file's `parameters` list, or `None` where `System.Text.Json` would
/// have thrown (anything malformed, or any value of the wrong type anywhere in what is
/// bound). A file that is JSON `null`, or has no list, is no entries.
fn parse_file(json: &str) -> Option<Vec<StoredParameter>> {
    let root: Value = serde_json::from_str(&make_lenient_json_strict(json)?).ok()?;

    let object = match root {
        Value::Null => return Some(Vec::new()),
        Value::Object(o) => o,
        _ => return None,
    };

    // `public int Version`: not nullable, so a null or a non-integer is a failure.
    if let Some(v) = property(&object, "version") {
        as_i32(v)?;
    }

    let list = match property(&object, "parameters") {
        None | Some(Value::Null) => return Some(Vec::new()),
        Some(Value::Array(items)) => items,
        Some(_) => return None,
    };

    let mut stored = Vec::with_capacity(list.len());

    for item in list {
        let entry = match item {
            Value::Null => continue,
            Value::Object(o) => o,
            _ => return None,
        };

        stored.push(StoredParameter {
            name: optional(property(entry, "name"), as_string)?,
            address: optional(property(entry, "address"), as_string)?,
            bytes: optional(property(entry, "bytes"), as_i32)?,
            units: optional(property(entry, "units"), as_string)?,
            scale: optional(property(entry, "scale"), as_f64)?,
            offset: optional(property(entry, "offset"), as_f64)?,
            digits: optional(property(entry, "digits"), as_i32)?,
            low: optional(property(entry, "low"), as_f64)?,
            high: optional(property(entry, "high"), as_f64)?,
            enabled: optional(property(entry, "enabled"), as_bool)?,
        });
    }

    Some(stored)
}

/// A property by name, ignoring case; the last one wins where a name repeats.
fn property<'a>(object: &'a Map<String, Value>, name: &str) -> Option<&'a Value> {
    object
        .iter()
        .filter(|(k, _)| eq_ignore_case(k, name))
        .map(|(_, v)| v)
        .next_back()
}

/// A nullable property: absent or `null` is `Some(None)`, a value of the right type is
/// `Some(Some(v))`, and anything else is `None` (the whole file refused).
fn optional<T>(value: Option<&Value>, convert: fn(&Value) -> Option<T>) -> Option<Option<T>> {
    match value {
        None | Some(Value::Null) => Some(None),
        Some(v) => convert(v).map(Some),
    }
}

fn as_string(v: &Value) -> Option<String> {
    v.as_str().map(str::to_string)
}

fn as_i32(v: &Value) -> Option<i32> {
    i32::try_from(v.as_i64()?).ok()
}

fn as_f64(v: &Value) -> Option<f64> {
    match v {
        Value::Number(n) => n.as_f64(),
        _ => None,
    }
}

fn as_bool(v: &Value) -> Option<bool> {
    v.as_bool()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_template_holds_the_two_confirmed_addresses() {
        let parameters = read(Some(template()));

        assert_eq!(parameters.len(), 2);
        assert_eq!(parameters[0].name, "Engine Speed");
        assert_eq!(parameters[0].address, 0x00000E);
        assert_eq!(parameters[0].bytes, 2);
        assert_eq!(parameters[0].scale, 0.25);
        assert_eq!(parameters[1].name, "Coolant");
        assert_eq!(parameters[1].address, 0x000008);
        assert_eq!(parameters[1].offset, -40.0);
        assert_eq!(parameters[1].units, "\u{b0}C");
    }

    #[test]
    fn a_bad_entry_costs_only_itself() {
        let json = r#"{ "parameters": [
            { "name": "Good", "address": "0x10" },
            { "name": "NoAddress" },
            { "name": "BadAddress", "address": "zz" },
            { "address": "0x20" },
            { "name": "TooBig", "address": "0x1000000" },
            { "name": "good", "address": "0x30" },
            { "name": "TooLong", "address": "0x40", "bytes": 5 },
            { "name": "ZeroScale", "address": "0x50", "scale": 0 },
            { "name": "Off", "address": "50", "enabled": false },
        ] }"#;

        let all = read(Some(json));
        let names: Vec<&str> = all.iter().map(|p| p.name.as_str()).collect();
        assert_eq!(names, ["Good", "Off"]);
        assert_eq!(all[1].address, 0x50);

        let on: Vec<String> = enabled(Some(json)).into_iter().map(|p| p.name).collect();
        assert_eq!(on, ["Good"]);
    }

    // The C# threw a NullReferenceException on a null entry and lost the whole file.
    #[test]
    fn a_null_entry_is_skipped_and_the_rest_of_the_file_is_read() {
        let json = r#"{ "parameters": [ null, { "name": "Good", "address": "0x10" } ] }"#;

        let names: Vec<String> = read(Some(json)).into_iter().map(|p| p.name).collect();

        assert_eq!(names, ["Good"]);
    }

    #[test]
    fn property_names_ignore_case_and_comments_are_skipped() {
        let json = "// a note\n{ \"Parameters\": [ /* x */ { \"NAME\": \" Knock \", \"Address\": \"0X1f\", \"Units\": \" dB \" } ] }";

        let got = read(Some(json));
        assert_eq!(got.len(), 1);
        assert_eq!(got[0].name, "Knock");
        assert_eq!(got[0].address, 0x1F);
        assert_eq!(got[0].units, "dB");
    }

    #[test]
    fn rubbish_is_no_parameters() {
        assert!(read(None).is_empty());
        assert!(read(Some("  ")).is_empty());
        assert!(read(Some("not json")).is_empty());
        assert!(read(Some("[]")).is_empty());
        assert!(read(Some("null")).is_empty());
        assert!(read(Some("{}")).is_empty());
        // A wrongly-typed value anywhere refuses the whole file, as the C#'s deserialiser did.
        assert!(read(Some(r#"{"parameters":[{"name":"a","address":"1","bytes":"2"}]}"#)).is_empty());
    }

    #[test]
    fn addresses_are_hex_with_or_without_the_prefix() {
        assert_eq!(parse_address(Some("0x00000E")), Some(0x0E));
        assert_eq!(parse_address(Some("0E")), Some(0x0E));
        assert_eq!(parse_address(Some("  0Xff  ")), Some(0xFF));
        assert_eq!(parse_address(Some("FFFFFF")), Some(0xFF_FFFF));
        assert_eq!(parse_address(Some("1000000")), None);
        assert_eq!(parse_address(Some("FFFFFFFF")), None);
        assert_eq!(parse_address(Some("123456789")), None);
        assert_eq!(parse_address(Some("0x")), None);
        assert_eq!(parse_address(Some("")), None);
        assert_eq!(parse_address(Some("-1")), None);
        assert_eq!(parse_address(None), None);
    }

    #[test]
    fn the_file_is_written_the_first_time_and_then_read() {
        let folder = std::env::temp_dir().join(format!("olv-ssm-{}", std::process::id()));
        let folder_text = folder.to_str().unwrap();
        let _ = std::fs::remove_dir_all(&folder);

        let first = read_from(folder_text).unwrap();
        assert_eq!(first.len(), 2);
        assert_eq!(std::fs::read_to_string(path_in(folder_text)).unwrap(), template());

        let _ = std::fs::remove_dir_all(&folder);
        assert!(read_from("  ").is_err());
    }
}
