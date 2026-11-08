//! The definitions package against the C# on the real tunes and INIs in the corpus
//! (rust/oracle/expected/corpus/w2-definitions/definitions.json, gitignored).
//!
//! Per INI: what `DefinitionFile` inspects (signature, pages, channels, problem, name) and
//! every constant `DangerousConstants` flags in its layout. Per tune: everything `MsqFile`
//! reads (header, symbols, every constant and PC variable in order), the INI the catalogue
//! matches, the `DefinitionNeed` described for its signature, and what `DangerousConstants`
//! flags among the tune's settings against each INI.

use olv_core::definitions::dangerous_constants;
use olv_core::definitions::definition_file;
use olv_core::definitions::definition_need;
use olv_core::definitions::msq_file::MsqFile;
use olv_core::ini::ini_catalog::{self, IniFile};
use olv_core::ini::msq_ini;
use olv_core::ini::tune_layout::{self, TuneLayout};
use olv_core::model::tuning_text;
use serde::Deserialize;
use std::path::Path;

#[derive(Deserialize)]
struct Flag {
    name: String,
    role: String,
}

#[derive(Deserialize)]
struct Need {
    family: String,
    signature: String,
    version: String,
    filename: String,
    #[serde(rename = "where")]
    where_from: String,
    sources: Vec<String>,
    nearby: Vec<Near>,
}

#[derive(Deserialize)]
struct Near {
    name: String,
    signature: String,
}

#[derive(Deserialize)]
struct Against {
    ini: String,
    flagged: Vec<Flag>,
}

#[derive(Deserialize)]
struct Case {
    kind: String,
    file: String,
    signature: String,
    // ini
    #[serde(default)]
    pages: usize,
    #[serde(default)]
    channels: usize,
    #[serde(default)]
    problem: String,
    #[serde(default)]
    name_for: String,
    #[serde(default)]
    flagged: Vec<Flag>,
    // tune
    #[serde(default)]
    firmware: String,
    #[serde(default)]
    author: String,
    #[serde(default)]
    comment: String,
    #[serde(default)]
    written_at: String,
    #[serde(default)]
    page_count: i32,
    #[serde(default)]
    symbols: Vec<String>,
    #[serde(default)]
    is_empty: bool,
    #[serde(default)]
    values: Vec<[String; 2]>,
    #[serde(default)]
    pc_variables: Vec<[String; 2]>,
    #[serde(default)]
    ini_match: Option<String>,
    #[serde(default)]
    need: Option<Need>,
    #[serde(default)]
    against: Vec<Against>,
}

fn flags<'a>(layout: &TuneLayout, names: impl Iterator<Item = &'a str>) -> Vec<(String, String)> {
    names
        .filter_map(|n| dangerous_constants::find(layout, n).map(|r| (n.to_string(), r.name().to_string())))
        .collect()
}

fn expect_flags(actual: Vec<(String, String)>, expected: &[Flag], what: &str) {
    let expected: Vec<(String, String)> = expected.iter().map(|f| (f.name.clone(), f.role.clone())).collect();
    assert_eq!(actual, expected, "{what}");
}

#[test]
fn every_ini_and_tune_in_the_corpus_matches_the_csharp() {
    let Some(cases) = olv_oracle::corpus_cases::<Case>("w2-definitions", "definitions") else {
        return;
    };
    assert!(!cases.is_empty());

    let corpus = olv_oracle::corpus_dir();

    // The same inputs the emitter read: every .ini in definitions/, in ordinal name order.
    let mut ini_paths: Vec<_> = std::fs::read_dir(corpus.join("definitions"))
        .expect("corpus definitions")
        .filter_map(|e| e.ok().map(|e| e.path()))
        .filter(|p| p.extension().is_some_and(|x| x.eq_ignore_ascii_case("ini")))
        .collect();
    ini_paths.sort_by_key(|p| p.file_name().map(|n| n.to_string_lossy().into_owned()));

    let mut inis: Vec<(String, TuneLayout)> = Vec::new();
    let mut catalogue: Vec<IniFile> = Vec::new();
    for path in &ini_paths {
        let text = tuning_text::read(path).unwrap();
        inis.push((
            path.file_name().unwrap().to_string_lossy().into_owned(),
            tune_layout::read(&text, None),
        ));
        let p = path.to_string_lossy().into_owned();
        let signature = ini_catalog::read_signature(&p).unwrap_or_default();
        catalogue.push(IniFile::new(p, signature));
    }

    for case in &cases {
        let what = format!("{} {}", case.kind, case.file);

        if case.kind == "ini" {
            let path = corpus.join("definitions").join(&case.file);
            let text = tuning_text::read(&path).unwrap();
            let found = definition_file::inspect(&text);
            assert_eq!(found.signature, case.signature, "{what} signature");
            assert_eq!(found.pages, case.pages, "{what} pages");
            assert_eq!(found.channels, case.channels, "{what} channels");
            assert_eq!(found.problem, case.problem, "{what} problem");
            assert_eq!(
                definition_file::name_for(&found.signature),
                case.name_for,
                "{what} name"
            );
            // The channel count is what the realtime reader yields with default symbols.
            assert_eq!(msq_ini::read_output_channels(&text, None).fields.len(), case.channels);

            let (_, layout) = inis.iter().find(|(n, _)| *n == case.file).expect("ini loaded");
            let names = layout.constants.iter().map(|c| c.name.as_str());
            expect_flags(flags(layout, names), &case.flagged, &format!("{what} flagged"));
            continue;
        }

        let msq = MsqFile::read_file(&corpus.join("tunes").join(&case.file)).unwrap();
        assert_eq!(msq.signature, case.signature, "{what} signature");
        assert_eq!(msq.firmware, case.firmware, "{what} firmware");
        assert_eq!(msq.author, case.author, "{what} author");
        assert_eq!(msq.comment, case.comment, "{what} comment");
        assert_eq!(msq.written_at, case.written_at, "{what} written_at");
        assert_eq!(msq.page_count, case.page_count, "{what} page_count");
        assert_eq!(msq.is_empty(), case.is_empty, "{what} is_empty");

        // The set is compared both ways: everything expected is in, and the count is equal.
        for symbol in &case.symbols {
            assert!(msq.symbols.contains(symbol), "{what} symbol {symbol}");
        }
        let mut mine: Vec<String> = case.symbols.clone();
        mine.sort();
        mine.dedup();
        assert_eq!(mine.len(), case.symbols.len(), "{what} symbols distinct");
        // And the other way: nothing in the Rust's set that the C# didn't read.
        assert_eq!(msq.symbols.len(), case.symbols.len(), "{what} symbol count");

        let actual: Vec<[String; 2]> = msq.values.iter().map(|(k, v)| [k.to_string(), v.to_string()]).collect();
        assert_eq!(actual.len(), case.values.len(), "{what} value count");
        assert!(actual == case.values, "{what} values differ");

        let actual: Vec<[String; 2]> = msq
            .pc_variables
            .iter()
            .map(|(k, v)| [k.to_string(), v.to_string()])
            .collect();
        assert!(actual == case.pc_variables, "{what} pc variables differ");

        let matched = ini_catalog::match_signature(&msq.signature, &catalogue)
            .map(|i| Path::new(&i.path).file_name().unwrap().to_string_lossy().into_owned());
        assert_eq!(matched, case.ini_match, "{what} ini match");

        let need = definition_need::describe(std::slice::from_ref(&msq.signature), &catalogue, "defs");
        let expected = case.need.as_ref().expect("a tune has a need");
        assert_eq!(need.family, expected.family, "{what} family");
        assert_eq!(need.signature, expected.signature, "{what} need signature");
        assert_eq!(need.version, expected.version, "{what} version");
        assert_eq!(need.filename, expected.filename, "{what} filename");
        assert_eq!(need.where_from, expected.where_from, "{what} where");
        assert_eq!(need.sources, expected.sources, "{what} sources");
        let near: Vec<(String, String)> = need
            .nearby
            .iter()
            .map(|n| (n.name.clone(), n.signature.clone()))
            .collect();
        let want: Vec<(String, String)> = expected
            .nearby
            .iter()
            .map(|n| (n.name.clone(), n.signature.clone()))
            .collect();
        assert_eq!(near, want, "{what} nearby");

        assert_eq!(case.against.len(), inis.len(), "{what} against");
        for (against, (name, layout)) in case.against.iter().zip(&inis) {
            assert_eq!(&against.ini, name);
            expect_flags(
                flags(layout, msq.values.keys()),
                &against.flagged,
                &format!("{what} against {name}"),
            );
        }
    }
}
