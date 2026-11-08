//! Ported from `DefinitionImport.cs`. Owned by package `w2-definitions`.
//!
//! Taking a definition somebody (or something) handed over, and deciding whether to keep it.
//!
//! This is the one place in the application where an outside file becomes part of how an
//! engine is read and written. A definition decides which byte is the rev limiter; the
//! dangerous-constant guard matches on names this file declares; a write lands where this
//! file says it should. So the checks here are the gate, and they all run before anything
//! touches the definitions folder: that folder is scanned recursively on every connection, so
//! a bad file allowed into it is wrong for ever, not once.
//!
//! What it cannot do is decide whether the file is trustworthy in a larger sense. What it can
//! do, and does, is refuse anything that does not declare the signature the ECU actually
//! reported, refuse anything that is not a usable definition at all, and write down where the
//! file came from so the question can be asked later by somebody who can answer it.

use super::definition_file::{self, LARGEST_BYTES};
use crate::base::safe_folder::{SafeFolder, SafeFolderError};
use crate::ini::ini_catalog;
use crate::model::{eq_ignore_case, tuning_text};
use crate::{Error, Result};
use serde::Deserialize;
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

/// The record of everything installed this way, beside the definitions themselves.
pub const LEDGER: &str = "imported-definitions.json";

/// Where an installed definition came from, kept beside it.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DefinitionProvenance {
    /// The file, as it sits in the definitions folder.
    pub name: String,
    /// What it declares itself to be.
    pub signature: String,
    /// Where it was fetched from, or how it arrived.
    pub source: String,
    /// The bytes that were accepted (lowercase hex SHA-256), so a later copy can be told
    /// apart.
    pub sha256: String,
    /// When it was installed.
    pub at: SystemTime,
    /// What the ECU said, at the time this was fetched for it.
    pub for_identity: Vec<String>,
}

/// What importing a definition did, or why it was refused.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DefinitionImportResult {
    pub problem: String,
    pub path: String,
    pub provenance: Option<DefinitionProvenance>,
}

impl DefinitionImportResult {
    pub fn accepted(&self) -> bool {
        self.problem.is_empty()
    }

    fn refused(problem: impl Into<String>) -> Self {
        Self {
            problem: problem.into(),
            path: String::new(),
            provenance: None,
        }
    }
}

/// `text.Length` counts UTF-16 units; the UTF-8 length is never smaller, so only a text
/// already over the limit in bytes needs counting.
fn too_large(text: &str) -> bool {
    text.len() as u64 > LARGEST_BYTES && text.encode_utf16().count() as u64 > LARGEST_BYTES
}

/// Keeps `text` as a definition, if it is one and if it is the one this ECU asked for.
///
/// `must_match` is what the ECU reported. When any of these is the signature the file
/// declares, it is kept; when none is, it is refused. Empty skips the check, for importing a
/// definition with nothing plugged in. `name` is what to call it, or empty to name it after
/// its signature.
///
/// Fails (as the C# throws) with [`Error::Argument`] only for an unusable `folder`.
pub fn keep(
    text: &str,
    folder: &str,
    must_match: &[String],
    source: &str,
    name: &str,
) -> Result<DefinitionImportResult> {
    if too_large(text) {
        return Ok(DefinitionImportResult::refused(
            "that is far larger than any firmware definition",
        ));
    }

    let found = definition_file::inspect(text);
    if !found.is_usable() {
        return Ok(DefinitionImportResult::refused(found.problem));
    }

    // The check that matters. Exactly, not the looser matching a live session is allowed,
    // which would accept a definition declaring nothing but "speeduino" and install it as the
    // answer for every Speeduino from now on.
    if !must_match.is_empty()
        && !must_match
            .iter()
            .any(|said| ini_catalog::same_signature(&found.signature, said))
    {
        return Ok(DefinitionImportResult::refused(format!(
            "it declares \"{}\", and the ECU reports \"{}\" \u{2014} a definition for different firmware \
             decodes every channel from the wrong place without ever failing, so this is refused",
            found.signature,
            must_match.join("\", \"")
        )));
    }

    let definitions = match SafeFolder::new(folder) {
        Ok(f) => f,
        Err(SafeFolderError::Argument(m)) => return Err(Error::Argument(m)),
        Err(SafeFolderError::Io(e)) => return Err(Error::Io(e)),
    };
    let filename = if name.is_empty() {
        definition_file::name_for(&found.signature)
    } else {
        name.to_string()
    };

    // Latin1, matching how a signature is read back out and how these files are written by
    // everyone who publishes them. A definition is ASCII but for the odd degree sign, and
    // round-tripping that through UTF-8 would change bytes the parser reads back.
    let path = match write_latin1(&definitions, &filename, text) {
        Ok(path) => path,
        Err(SafeFolderError::Argument(m)) => return Ok(DefinitionImportResult::refused(m)),
        Err(SafeFolderError::Io(e)) => {
            return Ok(DefinitionImportResult::refused(format!("it could not be written: {e}")));
        }
    };

    let provenance = DefinitionProvenance {
        name: path
            .file_name()
            .map(|n| n.to_string_lossy().into_owned())
            .unwrap_or_default(),
        signature: found.signature,
        source: source.to_string(),
        sha256: sha256_hex(&latin1(text)),
        at: SystemTime::now(),
        for_identity: must_match.to_vec(),
    };

    record(folder, &provenance);

    Ok(DefinitionImportResult {
        problem: String::new(),
        path: path.to_string_lossy().into_owned(),
        provenance: Some(provenance),
    })
}

/// The same, for a file already on disk.
pub fn keep_file(
    path: &str,
    folder: &str,
    must_match: &[String],
    source: &str,
    name: &str,
) -> Result<DefinitionImportResult> {
    if path.trim().is_empty() {
        return Err(Error::Argument(
            "The value cannot be an empty string or composed entirely of whitespace.".to_string(),
        ));
    }

    let file = Path::new(path);
    let Some(length) = std::fs::metadata(file).ok().filter(|m| m.is_file()).map(|m| m.len()) else {
        return Ok(DefinitionImportResult::refused(format!("there is no file at {path}")));
    };

    if length > LARGEST_BYTES {
        return Ok(DefinitionImportResult::refused(format!(
            "it is {} MB, far larger than any firmware definition",
            length / (1024 * 1024)
        )));
    }

    let text = match tuning_text::read(file) {
        Ok(text) => text,
        Err(Error::Io(e)) => {
            return Ok(DefinitionImportResult::refused(format!("it could not be read: {e}")));
        }
        Err(other) => return Err(other),
    };

    let file_name = file
        .file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_default();

    keep(
        &text,
        folder,
        must_match,
        if source.is_empty() { path } else { source },
        if name.is_empty() { &file_name } else { name },
    )
}

/// Everything installed this way, newest first.
pub fn imported(folder: &str) -> Vec<DefinitionProvenance> {
    let path = Path::new(folder).join(LEDGER);

    // The record is a courtesy; losing it must never cost a definition, so anything that
    // goes wrong reading it is an empty list.
    let Ok(text) = std::fs::read_to_string(&path) else {
        return Vec::new();
    };
    let Ok(rows) = serde_json::from_str::<Vec<Row>>(&text) else {
        return Vec::new();
    };

    let mut kept = Vec::with_capacity(rows.len());
    for row in rows {
        // A missing date is the default, as in the C# (DateTimeOffset's minimum, which
        // the epoch stands in for: both sort after every real entry). A date that is
        // there but unreadable is a JsonException in the C#, which costs the ledger.
        let at = if row.at.is_empty() {
            SystemTime::UNIX_EPOCH
        } else {
            match parse_time(&row.at) {
                Some(at) => at,
                None => return Vec::new(),
            }
        };
        kept.push(DefinitionProvenance {
            name: row.name,
            signature: row.signature,
            source: row.source,
            sha256: row.sha256,
            at,
            for_identity: row.for_identity,
        });
    }

    // OrderByDescending is stable.
    kept.sort_by_key(|p| std::cmp::Reverse(p.at));
    kept
}

/// Where a definition in use came from, when it was one of ours.
pub fn of(folder: &str, name: &str) -> Option<DefinitionProvenance> {
    imported(folder).into_iter().find(|p| eq_ignore_case(&p.name, name))
}

/// One ledger entry. Every field defaults when absent, as System.Text.Json fills a
/// missing property with its default: an entry missing one field must not cost the
/// whole ledger.
#[derive(Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
struct Row {
    name: String,
    signature: String,
    source: String,
    sha256: String,
    at: String,
    for_identity: Vec<String>,
}

fn record(folder: &str, provenance: &DefinitionProvenance) {
    let mut kept = vec![provenance.clone()];
    kept.extend(
        imported(folder)
            .into_iter()
            .filter(|p| !eq_ignore_case(&p.name, &provenance.name)),
    );

    // The definition is the useful part; the record is a courtesy.
    let _ = std::fs::write(Path::new(folder).join(LEDGER), ledger_json(&kept));
}

// ----- the ledger's JSON, written the way System.Text.Json indents and escapes it ----------

fn ledger_json(items: &[DefinitionProvenance]) -> String {
    if items.is_empty() {
        return "[]".to_string();
    }

    let mut out = String::from("[\n");
    for (i, p) in items.iter().enumerate() {
        out.push_str("  {\n");
        out.push_str(&format!("    \"name\": {},\n", json_string(&p.name)));
        out.push_str(&format!("    \"signature\": {},\n", json_string(&p.signature)));
        out.push_str(&format!("    \"source\": {},\n", json_string(&p.source)));
        out.push_str(&format!("    \"sha256\": {},\n", json_string(&p.sha256)));
        out.push_str(&format!("    \"at\": {},\n", json_string(&format_time(p.at))));
        if p.for_identity.is_empty() {
            out.push_str("    \"forIdentity\": []\n");
        } else {
            out.push_str("    \"forIdentity\": [\n");
            for (j, said) in p.for_identity.iter().enumerate() {
                out.push_str(&format!("      {}", json_string(said)));
                out.push_str(if j + 1 < p.for_identity.len() { ",\n" } else { "\n" });
            }
            out.push_str("    ]\n");
        }
        out.push_str(if i + 1 < items.len() { "  },\n" } else { "  }\n" });
    }
    out.push(']');
    out
}

/// A JSON string as the default `System.Text.Json` encoder writes it: quotes, `<>&'+` and
/// everything outside printable ASCII as `\uXXXX`.
fn json_string(s: &str) -> String {
    let mut out = String::from("\"");
    for c in s.chars() {
        match c {
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            '\u{8}' => out.push_str("\\b"),
            '\u{c}' => out.push_str("\\f"),
            ' '..='~' if !matches!(c, '"' | '<' | '>' | '&' | '\'' | '+' | '`') => out.push(c),
            _ => {
                let mut units = [0u16; 2];
                for unit in c.encode_utf16(&mut units) {
                    out.push_str(&format!("\\u{unit:04X}"));
                }
            }
        }
    }
    out.push('"');
    out
}

const TICKS_PER_SECOND: i128 = 10_000_000;

/// `DateTime` round-trip text: `2026-11-07T12:34:56.1234567Z`, with trailing zeros of the
/// fraction dropped and the whole fraction when it is zero.
fn format_time(t: SystemTime) -> String {
    let ticks = t.duration_since(UNIX_EPOCH).map(|d| d.as_nanos() / 100).unwrap_or(0) as i128;
    let secs = ticks.div_euclid(TICKS_PER_SECOND) as i64;
    let fraction = ticks.rem_euclid(TICKS_PER_SECOND);

    let days = secs.div_euclid(86_400);
    let in_day = secs.rem_euclid(86_400);
    let (y, m, d) = civil_from_days(days);

    let mut out = format!(
        "{y:04}-{m:02}-{d:02}T{:02}:{:02}:{:02}",
        in_day / 3600,
        in_day % 3600 / 60,
        in_day % 60
    );
    if fraction != 0 {
        let digits = format!("{fraction:07}");
        out.push('.');
        out.push_str(digits.trim_end_matches('0'));
    }
    out.push('Z');
    out
}

/// The inverse, for the forms `System.Text.Json` writes a `DateTime` in.
fn parse_time(text: &str) -> Option<SystemTime> {
    let b = text.as_bytes();
    let num = |from: usize, len: usize| -> Option<i64> {
        let part = text.get(from..from + len)?;
        part.bytes().all(|c| c.is_ascii_digit()).then(|| part.parse().ok())?
    };

    if b.len() < 19
        || b[4] != b'-'
        || b[7] != b'-'
        || !(b[10] == b'T' || b[10] == b't')
        || b[13] != b':'
        || b[16] != b':'
    {
        return None;
    }
    let (y, mo, d) = (num(0, 4)?, num(5, 2)?, num(8, 2)?);
    let (h, mi, s) = (num(11, 2)?, num(14, 2)?, num(17, 2)?);
    if !(1..=12).contains(&mo) || !(1..=31).contains(&d) || h > 23 || mi > 59 || s > 59 {
        return None;
    }

    let mut i = 19;
    let mut fraction: i128 = 0;
    if b.get(i) == Some(&b'.') {
        let start = i + 1;
        i = start;
        while i < b.len() && b[i].is_ascii_digit() {
            i += 1;
        }
        let digits = &text[start..i];
        if digits.is_empty() || digits.len() > 7 {
            return None;
        }
        fraction = format!("{digits:0<7}").parse().ok()?;
    }

    let mut offset_secs: i64 = 0;
    match &text[i..] {
        "" | "Z" | "z" => {}
        rest if rest.len() == 6 && (rest.starts_with('+') || rest.starts_with('-')) => {
            let oh: i64 = rest[1..3].parse().ok()?;
            let om: i64 = rest[4..6].parse().ok()?;
            offset_secs = (oh * 3600 + om * 60) * if rest.starts_with('-') { -1 } else { 1 };
        }
        _ => return None,
    }

    let secs = days_from_civil(y, mo, d) * 86_400 + h * 3600 + mi * 60 + s - offset_secs;
    let ticks = i128::from(secs) * TICKS_PER_SECOND + fraction;
    let nanos = u64::try_from(ticks).ok()?.checked_mul(100)?;
    Some(UNIX_EPOCH + Duration::from_nanos(nanos))
}

/// Days since 1970-01-01 to a civil date (proleptic Gregorian).
fn civil_from_days(z: i64) -> (i64, i64, i64) {
    let z = z + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    (if m <= 2 { y + 1 } else { y }, m, d)
}

fn days_from_civil(y: i64, m: i64, d: i64) -> i64 {
    let y = if m <= 2 { y - 1 } else { y };
    let era = y.div_euclid(400);
    let yoe = y.rem_euclid(400);
    let doy = (153 * (if m > 2 { m - 3 } else { m + 9 }) + 2) / 5 + d - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146_097 + doe - 719_468
}

// ----- Latin1 and SHA-256 ---------------------------------------------------------------

/// `Encoding.Latin1.GetBytes`: one byte per UTF-16 unit, `?` where it does not fit.
fn latin1(text: &str) -> Vec<u8> {
    text.encode_utf16()
        .map(|u| if u <= 0xFF { u as u8 } else { b'?' })
        .collect()
}

/// Writes the bytes under `filename`, having proved with [`SafeFolder::resolve`] that the
/// name lands inside the folder.
fn write_latin1(folder: &SafeFolder, filename: &str, text: &str) -> std::result::Result<PathBuf, SafeFolderError> {
    let resolved = folder.resolve(filename)?;

    std::fs::create_dir_all(folder.folder())?;

    // Written beside it and renamed over, so a scan never meets half a definition.
    let mut temp = resolved.clone().into_os_string();
    temp.push(".olv-tmp");
    let temp = PathBuf::from(temp);
    let bytes = latin1(text);

    if let Err(e) = std::fs::write(&temp, &bytes).and_then(|()| std::fs::rename(&temp, &resolved)) {
        let _ = std::fs::remove_file(&temp);
        return Err(e.into());
    }

    Ok(resolved)
}

fn sha256_hex(data: &[u8]) -> String {
    const K: [u32; 64] = [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98,
        0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786,
        0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8,
        0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
        0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819,
        0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a,
        0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7,
        0xc67178f2,
    ];
    let mut h: [u32; 8] = [
        0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
    ];

    let mut message = data.to_vec();
    message.push(0x80);
    while message.len() % 64 != 56 {
        message.push(0);
    }
    message.extend_from_slice(&((data.len() as u64) * 8).to_be_bytes());

    let (blocks, _) = message.as_chunks::<64>();
    for chunk in blocks {
        let mut w = [0u32; 64];
        for (slot, word) in w.iter_mut().zip(chunk.as_chunks::<4>().0) {
            *slot = u32::from_be_bytes(*word);
        }
        for i in 16..64 {
            let s0 = w[i - 15].rotate_right(7) ^ w[i - 15].rotate_right(18) ^ (w[i - 15] >> 3);
            let s1 = w[i - 2].rotate_right(17) ^ w[i - 2].rotate_right(19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16].wrapping_add(s0).wrapping_add(w[i - 7]).wrapping_add(s1);
        }

        let [mut a, mut b, mut c, mut d, mut e, mut f, mut g, mut hh] = h;
        for i in 0..64 {
            let s1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
            let ch = (e & f) ^ (!e & g);
            let t1 = hh
                .wrapping_add(s1)
                .wrapping_add(ch)
                .wrapping_add(K[i])
                .wrapping_add(w[i]);
            let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
            let maj = (a & b) ^ (a & c) ^ (b & c);
            let t2 = s0.wrapping_add(maj);
            hh = g;
            g = f;
            f = e;
            e = d.wrapping_add(t1);
            d = c;
            c = b;
            b = a;
            a = t1.wrapping_add(t2);
        }
        for (slot, v) in h.iter_mut().zip([a, b, c, d, e, f, g, hh]) {
            *slot = slot.wrapping_add(v);
        }
    }

    h.iter().map(|word| format!("{word:08x}")).collect()
}

#[cfg(test)]
mod tests {
    use super::super::definition_file::tests::temp_path;
    use super::*;
    use crate::ini::ini_catalog::{match_signature, scan};

    /// A scratch folder that is removed when the test ends.
    struct Scratch(PathBuf);

    impl Scratch {
        fn new() -> Self {
            Scratch(temp_path("olv-defs"))
        }

        fn folder(&self) -> &str {
            self.0.to_str().unwrap()
        }
    }

    impl Drop for Scratch {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    const SPEEDUINO: &str = r#"[MegaTune]
   signature = "speeduino 202501"

[Constants]
page = 1
nPages = 1
pageSize = 32
pageIdentifier = "\$tsCanId\x01"
pageReadCommand = "r%2i%2o%2c"
pageChunkWrite  = "w%2i%2o%2c%v"
burnCommand     = "b%2i"
   crankingRPM = scalar, U16, 0, "rpm", 1, 0, 0, 10000, 0

[OutputChannels]
ochBlockSize = 4
ochGetCommand = "r\x00\x07%2o%2c"
   rpm = scalar, U16, 0, "rpm", 1, 0
"#;

    fn said(items: &[&str]) -> Vec<String> {
        items.iter().map(|s| s.to_string()).collect()
    }

    fn file_count(folder: &str) -> usize {
        std::fs::read_dir(folder).map(|d| d.count()).unwrap_or(0)
    }

    // ----- what is kept ------------------------------------------------------------

    #[test]
    fn a_definition_matching_what_the_ecu_said_is_kept() {
        let s = Scratch::new();

        let result = keep(
            SPEEDUINO,
            s.folder(),
            &said(&["Speeduino 2025.01.7", "speeduino 202501"]),
            "https://raw.githubusercontent.com/speeduino/speeduino/202501.7/reference/speeduino.ini",
            "",
        )
        .unwrap();

        assert!(result.accepted(), "{}", result.problem);
        assert!(Path::new(&result.path).is_file());
        assert_eq!(
            Path::new(&result.path).file_name().unwrap().to_str().unwrap(),
            "speeduino202501.ini"
        );

        // And it is found by the very next scan, which is the point of keeping it.
        let catalogue = scan(Some(&[s.folder().to_string()]));
        assert!(match_signature("speeduino 202501", &catalogue).is_some());
    }

    #[test]
    fn where_it_came_from_is_written_down() {
        let s = Scratch::new();
        let source = "https://speeduino.com/fw/202501.7.ini";

        keep(SPEEDUINO, s.folder(), &said(&["speeduino 202501"]), source, "").unwrap();

        let all = imported(s.folder());
        assert_eq!(all.len(), 1);
        let kept = &all[0];

        assert_eq!(kept.name, "speeduino202501.ini");
        assert_eq!(kept.signature, "speeduino 202501");
        assert_eq!(kept.source, source);
        assert_eq!(kept.sha256.len(), 64);
        assert!(kept.for_identity.contains(&"speeduino 202501".to_string()));
    }

    #[test]
    fn provenance_can_be_looked_up_by_the_name_in_use() {
        let s = Scratch::new();
        keep(SPEEDUINO, s.folder(), &said(&["speeduino 202501"]), "somewhere", "").unwrap();

        assert!(of(s.folder(), "speeduino202501.ini").is_some());
        assert!(of(s.folder(), "something-else.ini").is_none());
    }

    #[test]
    fn importing_with_nothing_plugged_in_skips_the_signature_check() {
        // Preparing before a drive, with no ECU to ask.
        let s = Scratch::new();

        assert!(keep(SPEEDUINO, s.folder(), &[], "a file I had", "").unwrap().accepted());
    }

    // ----- what is refused, and that nothing is left behind -------------------------

    #[test]
    fn a_definition_for_different_firmware_is_refused() {
        let s = Scratch::new();

        let result = keep(SPEEDUINO, s.folder(), &said(&["speeduino 202402"]), "wherever", "").unwrap();

        assert!(!result.accepted());
        assert!(result.problem.contains("wrong place"));
        assert_eq!(file_count(s.folder()), 0);
    }

    #[test]
    fn a_definition_declaring_only_a_prefix_is_refused() {
        // "speeduino" is a prefix of every Speeduino signature there has ever been. A live
        // session forgives that; installing it must not, or it becomes the answer for every
        // future connection.
        let s = Scratch::new();
        let loose = SPEEDUINO.replace("speeduino 202501", "speeduino");

        assert!(
            !keep(&loose, s.folder(), &said(&["speeduino 202501"]), "wherever", "")
                .unwrap()
                .accepted()
        );
    }

    #[test]
    fn something_that_is_not_a_definition_is_refused_before_it_is_written() {
        let s = Scratch::new();

        let result = keep(
            "<html><body>404 Not Found</body></html>",
            s.folder(),
            &[],
            "a bad fetch",
            "",
        )
        .unwrap();

        assert!(!result.accepted());
        assert_eq!(result.path, "");
        assert!(imported(s.folder()).is_empty());
    }

    #[test]
    fn a_truncated_definition_is_refused_even_with_the_right_signature() {
        let s = Scratch::new();
        let truncated = &SPEEDUINO[..SPEEDUINO.find("[Constants]").unwrap()];

        let result = keep(
            truncated,
            s.folder(),
            &said(&["speeduino 202501"]),
            "an interrupted download",
            "",
        )
        .unwrap();

        assert!(!result.accepted());
        assert!(result.problem.contains("no settings pages"));
    }

    #[test]
    fn a_name_that_tries_to_leave_the_folder_is_refused() {
        let s = Scratch::new();

        let result = keep(SPEEDUINO, s.folder(), &[], "wherever", r"..\..\evil.ini").unwrap();

        assert!(!result.accepted());
        assert_eq!(result.path, "");
    }

    // ----- from disk ---------------------------------------------------------------

    #[test]
    fn a_file_on_disk_is_imported_and_keeps_its_own_name() {
        let s = Scratch::new();
        let source = temp_path("olv-src").with_extension("ini");
        std::fs::write(&source, SPEEDUINO).unwrap();

        let result = keep_file(
            source.to_str().unwrap(),
            s.folder(),
            &said(&["speeduino 202501"]),
            "https://example.invalid/speeduino.ini",
            "",
        );
        let _ = std::fs::remove_file(&source);
        let result = result.unwrap();

        assert!(result.accepted(), "{}", result.problem);
        assert_eq!(Path::new(&result.path).file_name(), source.file_name(),);
        assert_eq!(
            result.provenance.unwrap().source,
            "https://example.invalid/speeduino.ini"
        );
    }

    #[test]
    fn a_file_that_is_not_there_is_refused_rather_than_throwing() {
        let s = Scratch::new();
        let absent = temp_path("olv-absent").with_extension("ini");

        let result = keep_file(absent.to_str().unwrap(), s.folder(), &[], "", "").unwrap();

        assert!(!result.accepted());
        assert!(result.problem.contains("no file"));
    }

    #[test]
    fn importing_the_same_definition_again_replaces_its_record_rather_than_doubling() {
        let s = Scratch::new();

        keep(SPEEDUINO, s.folder(), &said(&["speeduino 202501"]), "first", "").unwrap();
        keep(SPEEDUINO, s.folder(), &said(&["speeduino 202501"]), "second", "").unwrap();

        let all = imported(s.folder());
        assert_eq!(all.len(), 1);
        assert_eq!(all[0].source, "second");
    }

    // ----- the pieces the C# gets from the framework --------------------------------

    #[test]
    fn sha256_matches_the_known_vectors() {
        assert_eq!(
            sha256_hex(b""),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
        assert_eq!(
            sha256_hex(b"abc"),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        assert_eq!(
            sha256_hex(&[b'a'; 1000]),
            "41edece42d63e8d9bf515a9ba6932e1c20cbc9f5a5d134645adb5db1b9737ea3"
        );
    }

    #[test]
    fn latin1_keeps_a_degree_sign_as_one_byte_and_replaces_what_does_not_fit() {
        assert_eq!(latin1("\u{b0}F"), vec![0xB0, b'F']);
        assert_eq!(latin1("a\u{20ac}"), vec![b'a', b'?']);
    }

    #[test]
    fn the_time_round_trips_the_way_a_datetime_does() {
        let t = UNIX_EPOCH + Duration::from_nanos(1_794_000_000_123_456_700);
        let text = format_time(t);
        assert_eq!(text, "2026-11-06T21:20:00.1234567Z");
        assert_eq!(parse_time(&text), Some(t));
        assert_eq!(format_time(UNIX_EPOCH), "1970-01-01T00:00:00Z");
        assert_eq!(
            parse_time("2026-11-06T21:20:00.12Z"),
            Some(UNIX_EPOCH + Duration::from_nanos(1_794_000_000_120_000_000))
        );
        assert_eq!(parse_time("garbage"), None);
    }

    #[test]
    fn the_ledger_is_escaped_like_system_text_json() {
        assert_eq!(
            json_string("a+b&c\u{b0}\"\\"),
            "\"a\\u002Bb\\u0026c\\u00B0\\u0022\\\\\""
        );
    }

    // Checked against .NET 10: the default encoder escapes a backtick, and `Encoding.Latin1`
    // gives one `?` per UTF-16 unit, so a character outside the BMP is two.
    #[test]
    fn a_backtick_is_escaped_and_a_supplementary_character_is_two_question_marks() {
        assert_eq!(json_string("`x/~"), "\"\\u0060x/~\"");
        assert_eq!(latin1("a\u{1F600}b"), vec![b'a', b'?', b'?', b'b']);
    }

    #[test]
    fn a_ledger_entry_missing_fields_keeps_the_rest_of_the_ledger() {
        // System.Text.Json fills a missing property with its default; an entry written
        // by an older version without `forIdentity` must not cost every other entry.
        let dir = std::env::temp_dir().join(format!("olv-ledger-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(
            dir.join(LEDGER),
            r#"[{"name":"a.ini","signature":"s","source":"x","sha256":"00","at":"2026-01-02T03:04:05+00:00"},{"name":"b.ini"}]"#,
        )
        .unwrap();

        let got = imported(dir.to_str().unwrap());
        let _ = std::fs::remove_dir_all(&dir);

        assert_eq!(got.len(), 2);
        assert_eq!(got[0].name, "a.ini");
        assert!(got[0].for_identity.is_empty());
        assert_eq!(got[1].name, "b.ini");
    }
}
