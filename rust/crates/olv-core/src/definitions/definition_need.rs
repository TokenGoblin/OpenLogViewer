//! Ported from `DefinitionNeed.cs`. Owned by package `w2-definitions`.
//!
//! Working out what definition a connection needed, and where it comes from.
//!
//! One place, because two callers ask the same question for different audiences: the refusal
//! a person reads when a connection fails, and the answer an agent gets when it offers to go
//! and find the file. Saying it twice would let the two drift, and the agent's copy is the one
//! carrying a licence decision.
//!
//! Speeduino and rusEFI publish their definitions under a licence that permits fetching one;
//! MegaSquirt's does not, and says so. The MegaSquirt licence restricts the definition
//! specifically ("it is not permissable to use the INI file to tune other hardware") and
//! scopes redistribution to media accompanying a hardware sale. Its download path is also
//! defended against automation with a browser-agent check and a rotating token. So for that
//! family this returns no address at all and says where to look instead. The decision lives
//! here rather than in the agent, because an agent asked to be helpful will otherwise find a
//! way.

use super::definition_file;
use crate::ini::ini_catalog::IniFile;
use regex::Regex;
use std::sync::LazyLock;

/// A definition already on this machine that nearly, but does not, match.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DefinitionNearMiss {
    pub name: String,
    pub signature: String,
    pub path: String,
}

/// What is missing when an ECU cannot be connected to, said precisely enough to act on.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DefinitionNeed {
    /// Everything the ECU said about itself.
    pub identity: Vec<String>,
    /// "Speeduino", "rusEFI", "MegaSquirt", or empty when unrecognised.
    pub family: String,
    /// The reply that looks like the signature a definition would declare.
    pub signature: String,
    /// The reply carrying a patch level, where the family has one.
    pub version: String,
    /// What the file is conventionally called, so it can be recognised.
    pub filename: String,
    /// Definitions on this machine that nearly match, nearest first.
    pub nearby: Vec<DefinitionNearMiss>,
    /// Where a definition has to end up.
    pub folder: String,
    /// Where this family's definitions come from, in words.
    pub where_from: String,
    /// Addresses the file may be fetched from, nearest-first. Empty is a decision, not a gap.
    pub sources: Vec<String>,
}

/// "speeduino 202501": the form an INI declares. (`$` in .NET also matches before a final
/// newline, hence `\n?\z`.)
static SPEEDUINO_SIGNATURE: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?i)^speeduino\s+(\d{6}[A-Za-z0-9.\-]*)\n?\z").expect("a valid pattern"));

/// "Speeduino 2025.01.7": the form carrying the patch level.
static SPEEDUINO_VERSION: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?i)^speeduino\s+(\d{4}\.\d{2}(?:\.\d+)?)\n?\z").expect("a valid pattern"));

/// What was needed, given what the ECU said and what is on this machine.
pub fn describe(identity: &[String], catalogue: &[IniFile], folder: &str) -> DefinitionNeed {
    let family = family_of(identity);
    let signature = signature_of(identity, family);
    let version = version_of(identity, family, &signature);

    let filename = if signature.is_empty() {
        String::new()
    } else {
        definition_file::name_for(&signature)
    };

    DefinitionNeed {
        identity: identity.to_vec(),
        family: family.to_string(),
        nearby: nearby(&signature, catalogue),
        folder: folder.to_string(),
        where_from: where_from(family).to_string(),
        sources: sources_for(family, &signature, &version),
        filename,
        signature,
        version,
    }
}

/// `string.Contains(value, StringComparison.OrdinalIgnoreCase)`.
fn contains_ignore_case(haystack: &str, needle: &str) -> bool {
    let hay: Vec<char> = haystack.chars().map(fold).collect();
    let pin: Vec<char> = needle.chars().map(fold).collect();

    pin.is_empty() || hay.windows(pin.len()).any(|w| w == pin.as_slice())
}

/// `string.StartsWith(value, StringComparison.OrdinalIgnoreCase)`.
fn starts_with_ignore_case(text: &str, prefix: &str) -> bool {
    let mut t = text.chars().map(fold);
    prefix.chars().map(fold).all(|p| t.next() == Some(p))
}

/// The simple uppercase mapping ordinal ignore-case comparison uses.
fn fold(c: char) -> char {
    let mut upper = c.to_uppercase();
    match (upper.next(), upper.next()) {
        (Some(u), None) => u,
        _ => c,
    }
}

/// Which firmware family said this, from anything it said.
pub fn family_of(identity: &[String]) -> &'static str {
    for said in identity {
        if contains_ignore_case(said, "speeduino") {
            return "Speeduino";
        }
        if contains_ignore_case(said, "rusEFI") {
            return "rusEFI";
        }

        if ["MS1", "MS2", "MS3", "MegaSquirt", "MicroSquirt"]
            .iter()
            .any(|m| contains_ignore_case(said, m))
        {
            return "MegaSquirt";
        }
    }

    ""
}

/// Which reply is the one a definition declares.
///
/// No reply says which it is, so this is a judgement per family rather than a rule: a
/// Speeduino answers both "Speeduino 2025.01.7" and "speeduino 202501" and only the second is
/// ever written in an INI. Where nothing is recognised the longest reply is the better guess,
/// since a signature carries more than a build string usually does.
fn signature_of(identity: &[String], family: &str) -> String {
    if identity.is_empty() {
        return String::new();
    }

    let looks_right = |s: &str| match family {
        "Speeduino" => SPEEDUINO_SIGNATURE.is_match(s),
        "rusEFI" => starts_with_ignore_case(s, "rusEFI") && s.contains('.'),
        "MegaSquirt" => contains_ignore_case(s, "Format") || contains_ignore_case(s, "comms"),
        _ => false,
    };

    if let Some(found) = identity.iter().find(|s| looks_right(s)) {
        return found.clone();
    }

    // OrderByDescending(Length).First(): the longest in UTF-16 units, the first of equals.
    let mut longest = &identity[0];
    for s in &identity[1..] {
        if s.encode_utf16().count() > longest.encode_utf16().count() {
            longest = s;
        }
    }
    longest.clone()
}

/// The reply carrying a patch level, where the family has one the signature does not.
///
/// This is not decoration. Speeduino's eight 202501.x releases all declare the same signature
/// and ship three different definitions between them, so the signature alone cannot say which
/// is wanted; the version can.
fn version_of(identity: &[String], family: &str, signature: &str) -> String {
    if family != "Speeduino" {
        return String::new();
    }

    identity
        .iter()
        .find(|s| SPEEDUINO_VERSION.is_match(s) && s.as_str() != signature)
        .cloned()
        .unwrap_or_default()
}

/// `char.ToLowerInvariant` over the characters that are not white space.
fn simplify(text: &str) -> String {
    text.chars()
        .filter(|c| !c.is_whitespace())
        .map(|c| {
            let mut lower = c.to_lowercase();
            match (lower.next(), lower.next()) {
                (Some(l), None) => l,
                _ => c,
            }
        })
        .collect()
}

/// Definitions already here that nearly match, nearest first.
///
/// The single most useful thing to tell somebody: "you have 202402, the board wants 202501"
/// turns what reads as an unsupported ECU into a one-version errand. Ranked by how much of the
/// signature they share, and anything sharing almost nothing is left out rather than padded
/// in.
fn nearby(signature: &str, catalogue: &[IniFile]) -> Vec<DefinitionNearMiss> {
    if signature.is_empty() {
        return Vec::new();
    }

    let wanted = simplify(signature);

    let mut candidates: Vec<(&IniFile, usize, String)> = catalogue
        .iter()
        .map(|i| (i, shared_prefix(&wanted, &simplify(&i.signature)), i.name()))
        .filter(|(i, shared, _)| *shared >= 4 && i.signature != signature)
        .collect();

    // OrderByDescending(shared).ThenBy(name, OrdinalIgnoreCase): stable.
    candidates.sort_by(|a, b| {
        b.1.cmp(&a.1).then_with(|| {
            let fold_units = |s: &str| {
                s.chars()
                    .map(fold)
                    .collect::<String>()
                    .encode_utf16()
                    .collect::<Vec<u16>>()
            };
            fold_units(&a.2).cmp(&fold_units(&b.2))
        })
    });

    candidates
        .into_iter()
        .take(3)
        .map(|(i, _, name)| DefinitionNearMiss {
            name,
            signature: i.signature.clone(),
            path: i.path.clone(),
        })
        .collect()
}

/// Characters in common at the start, counted in UTF-16 units as the C# indexes.
fn shared_prefix(a: &str, b: &str) -> usize {
    a.encode_utf16()
        .zip(b.encode_utf16())
        .take_while(|(x, y)| x == y)
        .count()
}

fn where_from(family: &str) -> &'static str {
    match family {
        "Speeduino" => {
            "Speeduino publishes its definition with each firmware release, and SpeedyLoader saves a \
             copy into your Downloads folder when it flashes. The file is the same one for every \
             board running that firmware."
        }
        "rusEFI" => {
            "rusEFI publishes a definition per board per build, and the rusEFI console keeps every one \
             it has fetched in .rusEFI\\ini_database."
        }
        "MegaSquirt" => {
            "MegaSquirt definitions ship inside the firmware download from msextra.com, and TunerStudio \
             installs around a hundred of them beside itself \u{2014} which is where this looks first. If \
             yours is not among them you will need the definition from the firmware package you \
             flashed, and the variant matching your hardware: ms3.ini for a plain MS3, ms3pro.ini for \
             a Pro, and so on."
        }
        _ => {
            "A firmware definition comes from whoever wrote the firmware \u{2014} usually inside the download \
             you flashed, or from the tuning software for that ECU."
        }
    }
}

/// Where the file may be fetched from, and deliberately nothing for MegaSquirt. See the note
/// at the top of this module.
fn sources_for(family: &str, signature: &str, version: &str) -> Vec<String> {
    match family {
        "Speeduino" => speeduino_sources(version, signature),
        "rusEFI" => rusefi_sources(signature),
        _ => Vec::new(),
    }
}

/// Built from the version rather than the signature, because the signature does not carry the
/// patch level and several releases share one. "Speeduino 2025.01.7" names tag 202501.7: drop
/// the word, then the first dot.
fn speeduino_sources(version: &str, signature: &str) -> Vec<String> {
    let mut tag = speeduino_tag(version);

    // No version to work from: the signature still names the series, which is better than
    // nothing, and its own tag exists.
    if tag.is_empty() {
        let Some(series) = SPEEDUINO_SIGNATURE.captures(signature) else {
            return Vec::new();
        };

        tag = series[1].to_string();
    }

    vec![
        format!("https://raw.githubusercontent.com/speeduino/speeduino/{tag}/reference/speeduino.ini"),
        format!("https://speeduino.com/fw/{tag}.ini"),
    ]
}

fn speeduino_tag(version: &str) -> String {
    let Some(parsed) = SPEEDUINO_VERSION.captures(version) else {
        return String::new();
    };

    // "2025.01.7" -> "202501.7": only the first dot goes.
    let digits = &parsed[1];
    match digits.find('.') {
        Some(first) => format!("{}{}", &digits[..first], &digits[first + 1..]),
        None => digits.to_string(),
    }
}

/// rusEFI's own lookup rule, which its console uses too: spaces and dots become path
/// separators under the online index.
fn rusefi_sources(signature: &str) -> Vec<String> {
    if !starts_with_ignore_case(signature, "rusEFI") {
        return Vec::new();
    }

    let mut parts: Vec<String> = signature
        .split([' ', '.'])
        .filter(|p| !p.is_empty())
        .map(str::to_string)
        .collect();
    if parts.len() < 2 {
        return Vec::new();
    }

    // Only the white-label leads, and only it is lowercased: board names are already lower and
    // branch names are case-sensitive directories.
    parts[0] = parts[0].to_lowercase();

    vec![format!("https://rusefi.com/online/ini/{}.ini", parts.join("/"))]
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::Path;

    fn said(items: &[&str]) -> Vec<String> {
        items.iter().map(|s| s.to_string()).collect()
    }

    fn describe_said(items: &[&str]) -> DefinitionNeed {
        describe(&said(items), &[], "defs")
    }

    /// A catalogue entry in a portable folder.
    fn ini(name: &str, signature: &str) -> IniFile {
        let path = Path::new("defs").join(name);
        IniFile::new(path.to_string_lossy().into_owned(), signature)
    }

    // ----- which firmware said it --------------------------------------------------

    #[test]
    fn each_family_is_recognised_from_what_it_says() {
        for (family, said) in [
            ("Speeduino", vec!["Speeduino 2025.01.7", "speeduino 202501"]),
            ("rusEFI", vec!["rusEFI master.2024.07.04.uaefi.1448555430"]),
            ("MegaSquirt", vec!["MS3 Format 0569.00"]),
        ] {
            assert_eq!(family_of(&super::tests::said(&said)), family);
        }
    }

    #[test]
    fn an_ecu_nobody_recognises_is_not_guessed_at() {
        let need = describe_said(&["SOMETHING ELSE 1.0"]);

        assert_eq!(need.family, "");
        assert!(need.sources.is_empty());
    }

    // ----- Speeduino: the version carries what the signature cannot -----------------

    #[test]
    fn a_speeduino_is_fetched_by_its_version_because_the_signature_does_not_pin_the_patch() {
        // The board on the bench. Eight 202501.x releases declare "speeduino 202501" and ship
        // three different definitions between them, so the signature alone would fetch a
        // coin toss. "2025.01.7" names tag 202501.7 exactly.
        let need = describe_said(&["Speeduino 2025.01.7", "speeduino 202501"]);

        assert_eq!(need.family, "Speeduino");
        assert_eq!(need.signature, "speeduino 202501");
        assert_eq!(need.version, "Speeduino 2025.01.7");

        assert_eq!(
            need.sources[0],
            "https://raw.githubusercontent.com/speeduino/speeduino/202501.7/reference/speeduino.ini"
        );
        assert_eq!(need.sources[1], "https://speeduino.com/fw/202501.7.ini");
    }

    #[test]
    fn a_speeduino_that_only_gave_its_signature_still_gets_the_series_tag() {
        // Worse than the above (the whole series rather than the exact patch) but a
        // definition from the right series beats none at all, and the tag exists.
        let need = describe_said(&["speeduino 202402"]);

        assert_eq!(need.version, "");
        assert!(need.sources[0].contains("/202402/reference/speeduino.ini"));
    }

    #[test]
    fn the_speeduino_filename_is_the_one_tuner_studio_would_use() {
        assert_eq!(describe_said(&["speeduino 202501"]).filename, "speeduino202501.ini");
    }

    // ----- rusEFI: the signature is the address -------------------------------------

    #[test]
    fn a_rusefi_signature_becomes_its_own_lookup_path() {
        // rusEFI's own published rule, which its console follows: spaces and dots become
        // separators, and only the white-label is lowercased.
        let need = describe_said(&["rusEFI 2023.01.08.proteus_f4.snap_14905"]);

        assert_eq!(
            need.sources,
            vec!["https://rusefi.com/online/ini/rusefi/2023/01/08/proteus_f4/snap_14905.ini".to_string()]
        );
    }

    #[test]
    fn a_rusefi_branch_name_survives_into_the_path() {
        let need = describe_said(&["rusEFI master.2024.07.04.uaefi.1448555430"]);

        assert_eq!(
            need.sources,
            vec!["https://rusefi.com/online/ini/rusefi/master/2024/07/04/uaefi/1448555430.ini".to_string()]
        );
    }

    // ----- MegaSquirt: no address, and that is the point ----------------------------

    #[test]
    fn a_megasquirt_is_never_given_an_address_to_fetch_from() {
        // Its licence restricts the definition itself ("not permissable to use the INI file
        // to tune other hardware") and scopes redistribution to media accompanying a hardware
        // sale. Its download path is defended against automation besides. The refusal to name
        // a source is a decision, and it belongs here rather than in whatever is asking.
        let need = describe_said(&["MS3 Format 0569.00"]);

        assert_eq!(need.family, "MegaSquirt");
        assert!(need.sources.is_empty());
    }

    #[test]
    fn a_megasquirt_is_told_where_to_look_instead() {
        let need = describe_said(&["MS3 Format 0569.00"]);

        assert!(need.where_from.to_lowercase().contains("tunerstudio"));
        assert!(need.where_from.to_lowercase().contains("ms3pro.ini"));
    }

    // ----- what is already here ----------------------------------------------------

    #[test]
    fn the_nearest_definition_on_this_machine_is_named() {
        // The one sentence that turns "unsupported ECU" into "your definition is one
        // firmware version stale".
        let here = [
            ini("speeduino202402.ini", "speeduino 202402"),
            ini("MS3Format0569.00.ini", "MS3 Format 0569.00"),
        ];

        let need = describe(&said(&["Speeduino 2025.01.7", "speeduino 202501"]), &here, "defs");

        assert_eq!(need.nearby.len(), 1);
        assert_eq!(need.nearby[0].name, "speeduino202402.ini");
        assert_eq!(need.nearby[0].signature, "speeduino 202402");
    }

    #[test]
    fn something_sharing_nothing_is_not_offered_as_nearby() {
        let here = [ini("MS3Format0569.00.ini", "MS3 Format 0569.00")];

        let need = describe(&said(&["speeduino 202501"]), &here, "defs");

        assert!(need.nearby.is_empty());
    }

    #[test]
    fn an_ecu_that_said_nothing_is_described_without_inventing_anything() {
        let need = describe(&[], &[], "defs");

        assert_eq!(need.signature, "");
        assert_eq!(need.filename, "");
        assert!(need.sources.is_empty());
        assert!(need.nearby.is_empty());
    }

    // ----- the edges the C# leaves implicit ----------------------------------------

    #[test]
    fn nearby_needs_four_shared_characters_ranks_them_and_keeps_three() {
        let here = [
            ini("b.ini", "speeduino 202401"),
            ini("a.ini", "speeduino 202402"),
            ini("c.ini", "speeduino 2025"),
            ini("d.ini", "speeduino 202501x"),
            ini("e.ini", "spee"),
            ini("f.ini", "spe"),
            ini("same.ini", "speeduino 202501"),
        ];

        let need = describe(&said(&["speeduino 202501"]), &here, "defs");
        let names: Vec<&str> = need.nearby.iter().map(|n| n.name.as_str()).collect();

        // d shares 15, c 12 ("speeduino2025"), a and b 11; the exact signature is left out.
        assert_eq!(names, ["d.ini", "c.ini", "a.ini"]);

        let only_short = describe(
            &said(&["speeduino 202501"]),
            &[ini("e.ini", "spee"), ini("f.ini", "spe")],
            "d",
        );
        assert_eq!(only_short.nearby.len(), 1);
        assert_eq!(only_short.nearby[0].name, "e.ini");
    }

    #[test]
    fn with_no_recognised_family_the_longest_reply_is_the_signature() {
        let need = describe_said(&["abc", "abcdef", "abcxyz"]);

        assert_eq!(need.signature, "abcdef");
        assert_eq!(need.version, "");
    }
}
