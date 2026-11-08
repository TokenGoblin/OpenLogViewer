//! `cargo xtask`: the port's bookkeeping, so neither the orchestrator nor an agent
//! spends tokens working out what a program can.
//!
//!   inventory        what each C# file declares, depends on and is tested by,
//!                    plus every .NET number format the code uses
//!   check            the package manifest against the code: every file assigned
//!                    once, nothing depending on a later wave
//!   brief <id>       the brief for one package's agent, ready to hand over
//!   status [--run]   one line per package; --run runs cargo test first
//!   layers           the dependency order the code itself implies: cycles that must
//!                    be ported together, and the earliest wave each file can go in
//!   corpus           copy the real logs, tunes and definitions named in corpus.toml

use regex::Regex;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::fmt::Write as _;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, ExitCode};

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let result = match args.first().map(String::as_str) {
        Some("inventory") => inventory(),
        Some("check") => check(),
        Some("brief") => match args.get(1) {
            Some(id) => brief(id),
            None => Err("usage: cargo xtask brief <package-id>".into()),
        },
        Some("status") => status(args.iter().any(|a| a == "--run")),
        Some("corpus") => corpus(),
        Some("layers") => layers(),
        Some("scaffold") => match args.get(1).and_then(|w| w.parse().ok()) {
            Some(wave) => scaffold(wave),
            None => Err("usage: cargo xtask scaffold <wave>".into()),
        },
        _ => Err("usage: cargo xtask <inventory | check | brief <id> | status [--run] | corpus>".into()),
    };
    match result {
        Ok(code) => code,
        Err(e) => {
            eprintln!("error: {e}");
            ExitCode::FAILURE
        }
    }
}

type Res = Result<ExitCode, String>;

// ----- paths ---------------------------------------------------------------------

fn repo() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("..").join("..")
}

fn rust_dir() -> PathBuf {
    repo().join("rust")
}

fn rel(p: &Path) -> String {
    let root = repo();
    let s = p.strip_prefix(&root).unwrap_or(p).to_string_lossy().replace('\\', "/");
    s.trim_start_matches("./").to_string()
}

fn cs_files(dir: &Path, out: &mut Vec<PathBuf>) {
    let Ok(entries) = fs::read_dir(dir) else { return };
    for e in entries.flatten() {
        let p = e.path();
        let name = e.file_name().to_string_lossy().to_string();
        if p.is_dir() {
            if name != "bin" && name != "obj" {
                cs_files(&p, out);
            }
        } else if name.ends_with(".cs") {
            out.push(p);
        }
    }
}

// ----- the manifest --------------------------------------------------------------

#[derive(Deserialize)]
struct Manifest {
    package: Vec<Package>,
}

#[derive(Deserialize, Clone)]
struct Package {
    id: String,
    wave: u32,
    #[serde(default = "port")]
    kind: String,
    #[serde(rename = "crate")]
    krate: String,
    module: String,
    model: String,
    #[serde(default = "default_effort")]
    effort: String,
    #[serde(default)]
    review: bool,
    files: Vec<String>,
}

fn port() -> String {
    "port".into()
}
fn default_effort() -> String {
    "default".into()
}

fn manifest() -> Result<Vec<Package>, String> {
    let text = fs::read_to_string(rust_dir().join("packages.toml")).map_err(|e| e.to_string())?;
    Ok(toml::from_str::<Manifest>(&text).map_err(|e| e.to_string())?.package)
}

/// A package's file name, as written in the manifest, to the file on disk.
fn resolve(name: &str) -> Option<PathBuf> {
    let root = repo();
    if name.contains('/') {
        let p = root.join(name);
        return p.exists().then_some(p);
    }
    for dir in ["src/OpenLogViewer.Core", "src/OpenLogViewer.App"] {
        for ext in [".cs", ".xaml.cs"] {
            let p = root.join(dir).join(format!("{name}{ext}"));
            if p.exists() {
                return Some(p);
            }
        }
    }
    None
}

// ----- reading C# ----------------------------------------------------------------

/// Code with comments removed and string literals emptied, except that the holes in
/// an interpolated string stay code: `$"{UnitConvert.Label(u)}"` still names a type.
/// Also returns the literals, which is where format strings live.
fn lex(src: &str) -> (String, Vec<String>) {
    let b: Vec<char> = src.chars().collect();
    let (mut code, mut lits) = (String::with_capacity(src.len()), Vec::new());
    let mut i = 0;
    while i < b.len() {
        let c = b[i];
        let next = b.get(i + 1).copied().unwrap_or('\0');
        if c == '/' && next == '/' {
            while i < b.len() && b[i] != '\n' {
                i += 1;
            }
        } else if c == '/' && next == '*' {
            i += 2;
            while i + 1 < b.len() && !(b[i] == '*' && b[i + 1] == '/') {
                i += 1;
            }
            i += 2;
        } else if c == '\'' {
            // char literal: '"' must not open a string
            i += 1;
            while i < b.len() && b[i] != '\'' {
                if b[i] == '\\' {
                    i += 1;
                }
                i += 1;
            }
            i += 1;
            code.push_str("' '");
        } else if c == '"' || ((c == '$' || c == '@') && (next == '"' || next == '$' || next == '@')) {
            let mut interp = false;
            let mut verbatim = false;
            while i < b.len() && b[i] != '"' {
                interp |= b[i] == '$';
                verbatim |= b[i] == '@';
                i += 1;
            }
            // raw literal """ ... """
            if b.get(i + 1) == Some(&'"') && b.get(i + 2) == Some(&'"') {
                let start = i + 3;
                i = start;
                while i + 2 < b.len() && !(b[i] == '"' && b[i + 1] == '"' && b[i + 2] == '"') {
                    i += 1;
                }
                lits.push(b[start..i.min(b.len())].iter().collect());
                i += 3;
                code.push_str("\"\"");
                continue;
            }
            i += 1;
            let mut lit = String::new();
            code.push('"');
            while i < b.len() {
                let ch = b[i];
                if !verbatim && ch == '\\' {
                    lit.push(ch);
                    if let Some(&n) = b.get(i + 1) {
                        lit.push(n);
                    }
                    i += 2;
                    continue;
                }
                if ch == '"' {
                    if verbatim && b.get(i + 1) == Some(&'"') {
                        i += 2;
                        continue;
                    }
                    break;
                }
                if interp && ch == '{' {
                    if b.get(i + 1) == Some(&'{') {
                        i += 2;
                        continue;
                    }
                    // a hole: code until the matching brace
                    let mut depth = 1;
                    let mut hole = String::new();
                    i += 1;
                    while i < b.len() && depth > 0 {
                        match b[i] {
                            '{' => depth += 1,
                            '}' => depth -= 1,
                            _ => {}
                        }
                        if depth > 0 {
                            hole.push(b[i]);
                        }
                        i += 1;
                    }
                    lit.push('{');
                    lit.push_str(&hole);
                    lit.push('}');
                    code.push(' ');
                    code.push_str(&hole);
                    code.push(' ');
                    continue;
                }
                lit.push(ch);
                i += 1;
            }
            i += 1;
            code.push('"');
            lits.push(lit);
        } else {
            code.push(c);
            i += 1;
        }
    }
    (code, lits)
}

#[derive(Serialize, Clone, Default)]
struct SourceFile {
    path: String,
    lines: usize,
    declares: BTreeSet<String>,
    #[serde(skip)]
    words: BTreeSet<String>,
    /// File -> the type names that make it a dependency.
    depends_on: BTreeMap<String, BTreeSet<String>>,
}

#[derive(Serialize, Clone, Default)]
struct TestFile {
    path: String,
    tests: usize,
    /// Source files whose types it uses, most-used first.
    covers: Vec<String>,
}

#[derive(Serialize, Default)]
struct Inventory {
    sources: BTreeMap<String, SourceFile>,
    tests: Vec<TestFile>,
    /// .NET format string -> number of uses.
    formats: BTreeMap<String, usize>,
}

fn read_inventory() -> Inventory {
    let root = repo();
    let decl = Regex::new(r"\b(?:class|struct|interface|enum|record)\s+(?:class\s+|struct\s+)?([A-Z]\w*)").unwrap();
    let word = Regex::new(r"\b[A-Z][A-Za-z0-9_]*\b").unwrap();
    let to_string = Regex::new(r#"\.ToString\(\s*"$"#).unwrap();
    let hole_fmt = Regex::new(r"^[^:?]*:([A-Za-z0-9#.,%]+)$").unwrap();
    let fmt_token = Regex::new(r"^(?:[CDEFGNPRX]\d{0,2}|[0#.,%]+)$").unwrap();

    let mut inv = Inventory::default();
    let mut paths = Vec::new();
    cs_files(&root.join("src"), &mut paths);

    for p in &paths {
        let src = fs::read_to_string(p).unwrap_or_default();
        let (code, lits) = lex(&src);
        let mut f = SourceFile {
            path: rel(p),
            lines: src.lines().filter(|l| !l.trim().is_empty()).count(),
            ..Default::default()
        };
        // Private nested types cannot be named from another file, so they are not
        // something another file can depend on.
        f.declares = decl
            .captures_iter(&code)
            .filter(|c| {
                let start = c.get(0).unwrap().start();
                let line_start = code[..start].rfind('\n').map_or(0, |i| i + 1);
                !code[line_start..start].contains("private")
            })
            .map(|c| c[1].to_string())
            .collect();
        // `namespace OpenLogViewer.App;` and `using` lines name namespaces, not types.
        let body: String = code
            .lines()
            .filter(|l| {
                let t = l.trim_start();
                !(t.starts_with("namespace ") || t.starts_with("using "))
            })
            .collect::<Vec<_>>()
            .join("\n");
        f.words = type_words(&word, &body);

        // Formats: "x.ToString("F1")" and "{value:F1}" holes.
        let raw_code: Vec<&str> = code.split('"').collect();
        let mut lit_iter = lits.iter();
        for (n, piece) in raw_code.iter().enumerate() {
            if n % 2 == 1 {
                continue;
            }
            if to_string.is_match(&format!("{piece}\""))
                && let Some(l) = lits.get(n / 2)
                && fmt_token.is_match(l)
            {
                *inv.formats.entry(l.clone()).or_default() += 1;
            }
        }
        for l in lit_iter.by_ref() {
            let mut depth = 0;
            let mut hole = String::new();
            for ch in l.chars() {
                match ch {
                    '{' => {
                        depth += 1;
                        hole.clear();
                    }
                    '}' if depth > 0 => {
                        depth -= 1;
                        if let Some(c) = hole_fmt.captures(&hole)
                            && fmt_token.is_match(&c[1])
                        {
                            *inv.formats.entry(c[1].to_string()).or_default() += 1;
                        }
                    }
                    _ if depth > 0 => hole.push(ch),
                    _ => {}
                }
            }
        }
        inv.sources.insert(f.path.clone(), f);
    }

    // Type -> the one file that declares it. A name declared in two files (a nested
    // `Entry` in each) says nothing about which one is meant, so it is dropped.
    let mut owner: BTreeMap<String, Option<String>> = BTreeMap::new();
    for f in inv.sources.values() {
        for t in &f.declares {
            owner
                .entry(t.clone())
                .and_modify(|o| {
                    if o.as_deref() != Some(&f.path) {
                        *o = None
                    }
                })
                .or_insert(Some(f.path.clone()));
        }
    }
    let owner: BTreeMap<String, String> = owner.into_iter().filter_map(|(k, v)| v.map(|v| (k, v))).collect();

    let paths_now: Vec<String> = inv.sources.keys().cloned().collect();
    for p in paths_now {
        let mut deps: BTreeMap<String, BTreeSet<String>> = BTreeMap::new();
        for w in &inv.sources[&p].words {
            if let Some(o) = owner.get(w)
                && *o != p
            {
                deps.entry(o.clone()).or_default().insert(w.clone());
            }
        }
        inv.sources.get_mut(&p).unwrap().depends_on = deps;
    }

    // Tests.
    let mut tpaths = Vec::new();
    cs_files(&root.join("tests"), &mut tpaths);
    let attr = Regex::new(r"\[(?:Fact|Theory)\b").unwrap();
    for p in tpaths {
        let src = fs::read_to_string(&p).unwrap_or_default();
        let (code, _) = lex(&src);
        let mut hits: BTreeMap<String, usize> = BTreeMap::new();
        for w in type_words(&word, &code) {
            if let Some(o) = owner.get(&w) {
                *hits.entry(o.clone()).or_default() += 1;
            }
        }
        let mut covers: Vec<(String, usize)> = hits.into_iter().collect();
        covers.sort_by(|a, b| b.1.cmp(&a.1).then(a.0.cmp(&b.0)));
        inv.tests.push(TestFile {
            path: rel(&p),
            tests: attr.find_iter(&code).count(),
            covers: covers.into_iter().map(|c| c.0).collect(),
        });
    }
    inv
}

/// Capitalised words that can be a type. Not after a dot (`ChannelRole.Engine` is a
/// member), and not before an opening bracket unless after `new` (`Subscribe(...)` is
/// a call, `new Subscribe(...)` is a type).
fn type_words(word: &Regex, code: &str) -> BTreeSet<String> {
    let bytes = code.as_bytes();
    word.find_iter(code)
        .filter(|m| {
            let before = code[..m.start()].trim_end();
            if before.ends_with('.') {
                return false;
            }
            let after = code[m.end()..].trim_start();
            if after.starts_with('(') && !before.ends_with("new") {
                return false;
            }
            let _ = bytes;
            true
        })
        .map(|m| m.as_str().to_string())
        .collect()
}

/// The package a test file belongs to: the one named after it if there is one
/// (`MlgReaderTests` -> `MlgReader`), else the one whose types it uses most.
fn test_owner(t: &TestFile, file_pkg: &BTreeMap<String, String>) -> Option<String> {
    let stem = Path::new(&t.path)
        .file_stem()?
        .to_string_lossy()
        .trim_end_matches("Tests")
        .to_string();
    // The interface tests exercise the view model through core types; what they
    // test is the interface, so only interface files can own them.
    let ui_test = t.path.contains("App.Tests");
    let covers: Vec<&String> = t
        .covers
        .iter()
        .filter(|f| !ui_test || f.contains("OpenLogViewer.App/"))
        .collect();
    let latest_used = t
        .covers
        .iter()
        .filter_map(|f| file_pkg.get(f))
        .map(|p| wave_of(p))
        .max();

    // Named after a file (MlgReaderTests -> MlgReader): that file's package, unless
    // the test also uses something ported later.
    for (file, pkg) in file_pkg {
        let fstem = Path::new(file)
            .file_name()?
            .to_string_lossy()
            .replace(".xaml.cs", "")
            .replace(".cs", "");
        // Plurals too: EngineFamilyTests tests EngineFamilies, ProbeTests tests Probes.
        let named = fstem == stem
            || fstem == format!("{stem}s")
            || stem.strip_suffix('y').is_some_and(|s| fstem == format!("{s}ies"));
        if named && latest_used.is_none_or(|w| wave_of(pkg) >= w) {
            return Some(pkg.clone());
        }
    }
    // A test can only run once everything it uses is ported, so it belongs to the
    // latest wave among the packages it touches; within that wave, the one it uses
    // most. (MaxxLogTests uses LogDocument from wave 0 and MaxxLogReader from wave 2:
    // it is a wave 2 test.)
    let latest = covers.iter().filter_map(|f| file_pkg.get(*f)).map(|p| wave_of(p)).max();
    let mut tally: BTreeMap<&String, usize> = BTreeMap::new();
    for (rank, f) in covers.iter().enumerate() {
        if let Some(pkg) = file_pkg.get(*f)
            && Some(wave_of(pkg)) == latest
        {
            // earlier = more-used; weight so the top file dominates ties
            *tally.entry(pkg).or_default() += covers.len() - rank;
        }
    }
    let best = tally.into_iter().max_by_key(|(_, n)| *n).map(|(p, _)| p.clone());

    // Workflow tests drive the view model through a harness and name no interface
    // type at all; the view model is what they test.
    best.or_else(|| {
        ui_test
            .then(|| {
                file_pkg
                    .iter()
                    .find(|(f, _)| f.ends_with("/MainViewModel.cs"))
                    .map(|(_, p)| p.clone())
            })
            .flatten()
    })
}

fn rust_sources(dir: &Path, out: &mut Vec<PathBuf>) {
    let Ok(entries) = fs::read_dir(dir) else { return };
    for e in entries.flatten() {
        let p = e.path();
        if p.is_dir() {
            rust_sources(&p, out);
        } else if p.extension().is_some_and(|x| x == "rs") {
            out.push(p);
        }
    }
}

/// A package counts as ported once none of its scaffolded stubs is still a stub (a
/// stub is the one-line `//! Ported from ...` that `xtask scaffold` writes).
fn package_is_ported(p: &Package) -> bool {
    let dir = rust_dir().join("crates").join(&p.krate).join("src").join(&p.module);
    let stubs: Vec<PathBuf> = p
        .files
        .iter()
        .filter(|f| !f.contains('/'))
        .map(|f| dir.join(format!("{}.rs", snake(f))))
        .filter(|f| f.exists())
        .collect();
    !stubs.is_empty() && stubs.iter().all(|f| count_rust_lines(f) > 1)
}

/// The wave a package id names: `w2-logs` is wave 2. `check` holds every id to this.
fn wave_of(id: &str) -> u32 {
    id.strip_prefix('w')
        .and_then(|s| s.split('-').next())
        .and_then(|n| n.parse().ok())
        .unwrap_or(u32::MAX)
}

fn file_to_package(pkgs: &[Package]) -> BTreeMap<String, String> {
    let mut m = BTreeMap::new();
    for p in pkgs {
        for f in &p.files {
            if let Some(path) = resolve(f) {
                m.insert(rel(&path), p.id.clone());
            }
        }
    }
    m
}

// ----- commands ------------------------------------------------------------------

fn inventory() -> Res {
    let inv = read_inventory();
    let out = rust_dir().join("target").join("xtask");
    fs::create_dir_all(&out).map_err(|e| e.to_string())?;
    let path = out.join("inventory.json");
    fs::write(&path, serde_json::to_string_pretty(&inv).unwrap()).map_err(|e| e.to_string())?;
    let lines: usize = inv.sources.values().map(|f| f.lines).sum();
    let tests: usize = inv.tests.iter().map(|t| t.tests).sum();
    println!(
        "{} source files, {} lines; {} test files, {} tests",
        inv.sources.len(),
        lines,
        inv.tests.len(),
        tests
    );
    println!("{} distinct .NET number formats:", inv.formats.len());
    let mut f: Vec<_> = inv.formats.iter().collect();
    f.sort_by(|a, b| b.1.cmp(a.1));
    let line: Vec<String> = f.iter().map(|(k, v)| format!("{k}×{v}")).collect();
    println!("  {}", line.join("  "));
    // The formats, for the C# reference generator: every one the code uses gets a
    // table of .NET's exact output, which the Rust `fmt` module must reproduce.
    let formats: Vec<&String> = inv.formats.keys().collect();
    let fpath = rust_dir().join("oracle").join("formats.txt");
    fs::create_dir_all(fpath.parent().unwrap()).map_err(|e| e.to_string())?;
    let body: Vec<&str> = formats.iter().map(|s| s.as_str()).collect();
    fs::write(&fpath, body.join("\n") + "\n").map_err(|e| e.to_string())?;
    println!("written to {} and {}", rel(&path), rel(&fpath));
    Ok(ExitCode::SUCCESS)
}

fn check() -> Res {
    let pkgs = manifest()?;
    let inv = read_inventory();
    let (mut errors, mut warnings) = (Vec::new(), Vec::new());

    let mut seen: BTreeMap<String, String> = BTreeMap::new();
    let mut ids = BTreeSet::new();
    for p in &pkgs {
        if wave_of(&p.id) != p.wave {
            errors.push(format!(
                "{} is in wave {}; its id must start w{}-",
                p.id, p.wave, p.wave
            ));
        }
        if !ids.insert(&p.id) {
            errors.push(format!("package id {} is used twice", p.id));
        }
        for f in &p.files {
            match resolve(f) {
                None => errors.push(format!("{}: no file called {f}", p.id)),
                Some(path) => {
                    let r = rel(&path);
                    if let Some(prev) = seen.insert(r.clone(), p.id.clone()) {
                        errors.push(format!("{r} is in both {prev} and {}", p.id));
                    }
                }
            }
        }
    }
    for path in inv.sources.keys() {
        if !seen.contains_key(path) {
            errors.push(format!("{path} is in no package"));
        }
    }

    let wave: BTreeMap<&String, u32> = pkgs.iter().map(|p| (&p.id, p.wave)).collect();
    let mut parallel: BTreeMap<(String, String), Vec<String>> = BTreeMap::new();
    for (path, f) in &inv.sources {
        let Some(mine) = seen.get(path) else { continue };
        for (d, why) in &f.depends_on {
            let why: Vec<&str> = why.iter().map(String::as_str).collect();
            let why = why.join(", ");
            let Some(theirs) = seen.get(d) else { continue };
            if theirs == mine {
                continue;
            }
            let (wm, wt) = (wave[mine], wave[theirs]);
            if wt > wm {
                errors.push(format!(
                    "{mine} (w{wm}) needs {theirs} (w{wt}): {} uses {why} from {}",
                    short(path),
                    short(d)
                ));
            } else if wt == wm {
                parallel
                    .entry((mine.clone(), theirs.clone()))
                    .or_default()
                    .push(format!("{} -> {}", short(path), short(d)));
            }
        }
    }
    for ((a, b), why) in parallel {
        warnings.push(format!("{a} needs {b} in the same wave: {}", why.join(", ")));
    }

    // A test skipped as `needs <package>` once that package is ported is a test nobody
    // will remember to switch back on. Two such tests were found with empty bodies.
    let needs = Regex::new(r#"ignore\s*=\s*"needs (w\d+-[a-z0-9-]+)"#).unwrap();
    let mut rust_files = Vec::new();
    rust_sources(&rust_dir().join("crates"), &mut rust_files);
    for f in &rust_files {
        let text = fs::read_to_string(f).unwrap_or_default();
        for c in needs.captures_iter(&text) {
            if let Some(p) = pkgs.iter().find(|p| p.id == c[1])
                && package_is_ported(p)
            {
                warnings.push(format!(
                    "{} skips a test as needing {}, which is ported now",
                    rel(f),
                    p.id
                ));
            }
        }
    }

    let file_pkg = file_to_package(&pkgs);
    for t in &inv.tests {
        if t.tests > 0 && test_owner(t, &file_pkg).is_none() {
            warnings.push(format!(
                "{} ({} tests) belongs to no package; name it after the file it tests",
                t.path, t.tests
            ));
        }
    }

    for w in &warnings {
        println!("warn:  {w}");
    }
    for e in &errors {
        println!("error: {e}");
    }
    println!(
        "{} packages, {} files assigned, {} errors, {} warnings",
        pkgs.len(),
        seen.len(),
        errors.len(),
        warnings.len()
    );
    Ok(if errors.is_empty() {
        ExitCode::SUCCESS
    } else {
        ExitCode::FAILURE
    })
}

/// Strongly connected components (Tarjan), in reverse topological order: every
/// component comes after the ones it depends on.
fn components(nodes: &[String], edges: &BTreeMap<String, BTreeSet<String>>) -> Vec<Vec<String>> {
    struct T<'a> {
        edges: &'a BTreeMap<String, BTreeSet<String>>,
        index: BTreeMap<String, usize>,
        low: BTreeMap<String, usize>,
        stack: Vec<String>,
        on: BTreeSet<String>,
        next: usize,
        out: Vec<Vec<String>>,
    }
    fn visit(t: &mut T, v: &str) {
        t.index.insert(v.into(), t.next);
        t.low.insert(v.into(), t.next);
        t.next += 1;
        t.stack.push(v.into());
        t.on.insert(v.into());
        let succ: Vec<String> = t.edges.get(v).map(|s| s.iter().cloned().collect()).unwrap_or_default();
        for w in succ {
            if !t.index.contains_key(&w) {
                visit(t, &w);
                let lw = t.low[&w];
                let lv = t.low.get_mut(v).unwrap();
                *lv = (*lv).min(lw);
            } else if t.on.contains(&w) {
                let iw = t.index[&w];
                let lv = t.low.get_mut(v).unwrap();
                *lv = (*lv).min(iw);
            }
        }
        if t.low[v] == t.index[v] {
            let mut comp = Vec::new();
            while let Some(w) = t.stack.pop() {
                t.on.remove(&w);
                let done = w == v;
                comp.push(w);
                if done {
                    break;
                }
            }
            comp.sort();
            t.out.push(comp);
        }
    }
    let mut t = T {
        edges,
        index: BTreeMap::new(),
        low: BTreeMap::new(),
        stack: vec![],
        on: BTreeSet::new(),
        next: 0,
        out: vec![],
    };
    for n in nodes {
        if !t.index.contains_key(n) {
            visit(&mut t, n);
        }
    }
    t.out
}

/// `MaxxUsbProtocol` -> `maxx_usb_protocol`, `Obd2Pids` -> `obd2_pids`.
fn snake(name: &str) -> String {
    let chars: Vec<char> = name.chars().collect();
    let mut out = String::new();
    for (i, &c) in chars.iter().enumerate() {
        if c.is_uppercase() && i > 0 {
            let prev = chars[i - 1];
            let next_lower = chars.get(i + 1).is_some_and(|n| n.is_lowercase());
            if prev.is_lowercase() || prev.is_ascii_digit() || (prev.is_uppercase() && next_lower) {
                out.push('_');
            }
        }
        out.extend(c.to_lowercase());
    }
    out
}

/// Appends `line` to a file unless it is already there.
fn ensure_line(path: &Path, line: &str, header: &str) -> Result<bool, String> {
    let text = fs::read_to_string(path).unwrap_or_else(|_| header.to_string());
    if text.lines().any(|l| l.trim() == line) {
        return Ok(false);
    }
    let mut text = text;
    if !text.ends_with('\n') {
        text.push('\n');
    }
    text.push_str(line);
    text.push('\n');
    fs::write(path, text).map_err(|e| e.to_string())?;
    Ok(true)
}

/// Creates everything a wave's agents would otherwise each have to create, so that no
/// two of them edit the same file: missing crates, the `pub mod` line for each module
/// in its crate's lib.rs, the module's mod.rs, and one stub file per C# file. Agents
/// then only fill in their own stubs. Running it twice changes nothing.
fn scaffold(wave: u32) -> Res {
    let pkgs = manifest()?;
    let mut made = Vec::new();
    for p in pkgs.iter().filter(|p| p.wave == wave && p.kind == "port") {
        let krate = rust_dir().join("crates").join(&p.krate);
        let src = krate.join("src");
        if !krate.join("Cargo.toml").exists() {
            fs::create_dir_all(&src).map_err(|e| e.to_string())?;
            let core = if p.krate == "olv-core" {
                String::new()
            } else {
                "olv-core = { path = \"../olv-core\" }\n".into()
            };
            fs::write(
                krate.join("Cargo.toml"),
                format!(
                    "[package]\nname = \"{}\"\nversion = \"0.1.0\"\nedition.workspace = true\nlicense.workspace = true\npublish.workspace = true\n\n[dependencies]\n{core}\n[dev-dependencies]\nolv-oracle = {{ path = \"../olv-oracle\" }}\nserde.workspace = true\n\n[lints]\nworkspace = true\n",
                    p.krate
                ),
            )
            .map_err(|e| e.to_string())?;
            made.push(format!("crate {}", p.krate));
        }
        let lib_header = format!(
            "//! {}: ported package by package from the C#; see `rust/PLAN.md`.\n\n",
            p.krate
        );
        if ensure_line(&src.join("lib.rs"), &format!("pub mod {};", p.module), &lib_header)? {
            made.push(format!("{}::{}", p.krate, p.module));
        }
        let dir = src.join(&p.module);
        // A module already written as a single file is left alone.
        if src.join(format!("{}.rs", p.module)).exists() {
            continue;
        }
        fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
        let mod_header = format!(
            "//! `{}`: see `rust/packages.toml` for which package owns each file.\n\n",
            p.module
        );
        for f in p.files.iter().filter(|f| !f.contains('/')) {
            let name = snake(
                f.trim_start_matches('I')
                    .get(..)
                    .filter(|_| f.starts_with('I') && f.chars().nth(1).is_some_and(char::is_uppercase))
                    .unwrap_or(f),
            );
            ensure_line(&dir.join("mod.rs"), &format!("pub mod {name};"), &mod_header)?;
            let stub = dir.join(format!("{name}.rs"));
            if !stub.exists() {
                fs::write(
                    &stub,
                    format!("//! Ported from `{f}.cs`. Owned by package `{}`.\n", p.id),
                )
                .map_err(|e| e.to_string())?;
                made.push(format!("{}/{name}.rs", rel(&dir)));
            }
        }
    }
    println!("wave {wave}: {} created", made.len());
    for m in made {
        println!("  {m}");
    }
    Ok(ExitCode::SUCCESS)
}

fn layers() -> Res {
    let inv = read_inventory();
    let nodes: Vec<String> = inv.sources.keys().cloned().collect();
    let edges: BTreeMap<String, BTreeSet<String>> = inv
        .sources
        .iter()
        .map(|(k, f)| (k.clone(), f.depends_on.keys().cloned().collect()))
        .collect();
    let comps = components(&nodes, &edges);

    // Depth of each component: 0 with no dependencies, else one past the deepest.
    let mut comp_of: BTreeMap<&str, usize> = BTreeMap::new();
    for (i, c) in comps.iter().enumerate() {
        for f in c {
            comp_of.insert(f, i);
        }
    }
    let mut depth = vec![0usize; comps.len()];
    for (i, c) in comps.iter().enumerate() {
        let mut d = 0;
        for f in c {
            for dep in &edges[f] {
                let j = comp_of[dep.as_str()];
                if j != i {
                    d = d.max(depth[j] + 1);
                }
            }
        }
        depth[i] = d;
    }

    println!("Cycles: files that use each other and must be ported by one agent\n");
    for (i, c) in comps.iter().enumerate().filter(|(_, c)| c.len() > 1) {
        let lines: usize = c.iter().map(|f| inv.sources[f].lines).sum();
        let names: Vec<String> = c.iter().map(|f| short(f)).collect();
        println!(
            "  [layer {}] {} files, {} lines: {}",
            depth[i],
            c.len(),
            lines,
            names.join(", ")
        );
    }
    println!("\nLayers: a file can be ported once every layer below it is done\n");
    let max = depth.iter().copied().max().unwrap_or(0);
    for d in 0..=max {
        let mut names: Vec<String> = Vec::new();
        let mut lines = 0;
        for (i, c) in comps.iter().enumerate().filter(|(i, _)| depth[*i] == d) {
            let _ = i;
            for f in c {
                names.push(short(f));
                lines += inv.sources[f].lines;
            }
        }
        names.sort();
        println!(
            "  layer {d:>2}: {} files, {lines} lines: {}",
            names.len(),
            names.join(" ")
        );
    }
    Ok(ExitCode::SUCCESS)
}

fn short(path: &str) -> String {
    Path::new(path)
        .file_name()
        .map(|n| n.to_string_lossy().replace(".xaml.cs", "").replace(".cs", ""))
        .unwrap_or_default()
}

fn brief(id: &str) -> Res {
    let pkgs = manifest()?;
    let p = pkgs.iter().find(|p| p.id == id).ok_or(format!("no package {id}"))?;
    let inv = read_inventory();
    let file_pkg = file_to_package(&pkgs);
    let pkg_of: BTreeMap<&String, &Package> = pkgs.iter().map(|p| (&p.id, p)).collect();

    let mut b = String::new();
    let _ = writeln!(b, "# Brief: {} (wave {}, {} effort)\n", p.id, p.wave, p.effort);
    let _ = writeln!(
        b,
        "Read `rust/PLAN.md` and `rust/CONVENTIONS.md` first; they hold the rules this brief does not repeat.\n"
    );
    if p.kind == "spec" {
        let _ = writeln!(
            b,
            "**Task:** write `rust/UI-SPEC.md` from the files below and the reference screenshots in `rust/oracle/screens/`. Describe behaviour, not WPF.\n"
        );
    } else {
        let _ = writeln!(
            b,
            "**Task:** port the C# below to `rust/crates/{}/src/{}` (a module, or a directory of modules), with its tests.\n",
            p.krate, p.module
        );
    }

    let _ = writeln!(b, "## Port these files\n");
    let mut total = 0;
    let mut needs: BTreeMap<String, BTreeSet<String>> = BTreeMap::new();
    for f in &p.files {
        let Some(path) = resolve(f) else { continue };
        let r = rel(&path);
        let lines = inv.sources.get(&r).map(|s| s.lines).unwrap_or_else(|| {
            fs::read_to_string(&path)
                .map(|s| s.lines().filter(|l| !l.trim().is_empty()).count())
                .unwrap_or(0)
        });
        total += lines;
        let _ = writeln!(b, "- `{r}` ({lines} lines)");
        if let Some(s) = inv.sources.get(&r) {
            for d in s.depends_on.keys() {
                if let Some(other) = file_pkg.get(d)
                    && other != &p.id
                {
                    needs.entry(other.clone()).or_default().insert(short(d));
                }
            }
        }
    }
    let _ = writeln!(b, "\nAbout {total} lines in all.\n");

    if !needs.is_empty() {
        let _ = writeln!(b, "## Already ported, use don't re-port\n");
        for (other, files) in &needs {
            let o = pkg_of[other];
            let files: Vec<String> = files.iter().cloned().collect();
            let _ = writeln!(
                b,
                "- `{}::{}` (from {}): {}",
                o.krate.replace('-', "_"),
                o.module,
                other,
                files.join(", ")
            );
        }
        let _ = writeln!(b);
    }

    let owned: Vec<&TestFile> = inv
        .tests
        .iter()
        .filter(|t| test_owner(t, &file_pkg).as_deref() == Some(&p.id))
        .collect();
    if !owned.is_empty() {
        let n: usize = owned.iter().map(|t| t.tests).sum();
        let _ = writeln!(b, "## Port these tests ({n})\n");
        for t in &owned {
            let _ = writeln!(b, "- `{}` ({} tests)", t.path, t.tests);
        }
        let _ = writeln!(b);
    }

    // Findings from earlier packages' reviews that this package must act on, so they
    // reach the agent that needs them without the orchestrator re-pasting them.
    if let Ok(notes) = fs::read_to_string(rust_dir().join("notes").join(format!("{}.md", p.id))) {
        let _ = writeln!(b, "## Carried over from earlier reviews\n\n{}\n", notes.trim());
    }

    let cases = rust_dir().join("oracle").join("expected").join(&p.id);
    let _ = writeln!(b, "## Gate\n");
    let _ = writeln!(
        b,
        "```\ncd rust\ncargo test -p {} {}\ncargo clippy -p {} -- -D warnings\n```\n",
        p.krate, p.module, p.krate
    );
    if cases.exists() {
        let _ = writeln!(
            b,
            "Reference outputs: `{}`. Compare through `olv_oracle`; never edit them.\n",
            rel(&cases)
        );
    }
    if p.review {
        let _ = writeln!(
            b,
            "An Opus review of the diff follows; flag anything you were unsure of in the report.\n"
        );
    }
    let _ = writeln!(
        b,
        "## Report (15 lines at most)\n\nFiles written, tests passing / failing, oracle cases passing, anything left undone and why. No code."
    );
    print!("{b}");
    Ok(ExitCode::SUCCESS)
}

fn status(run: bool) -> Res {
    let pkgs = manifest()?;
    let inv = read_inventory();
    let file_pkg = file_to_package(&pkgs);

    // crate -> module -> (passed, failed)
    let mut results: BTreeMap<String, BTreeMap<String, (usize, usize)>> = BTreeMap::new();
    if run {
        // Build every test binary and ask cargo where each one is and what it belongs
        // to, then run them one at a time. Reading `cargo test` output instead does not
        // work: its "Running" headers go to stderr and the results to stdout, so which
        // result came from which binary is lost.
        let build = Command::new("cargo")
            .args(["test", "--workspace", "--no-run", "--message-format=json"])
            .current_dir(rust_dir())
            .output()
            .map_err(|e| e.to_string())?;
        let mut binaries: Vec<(String, String, PathBuf, PathBuf)> = Vec::new(); // crate, target, exe, dir
        for l in String::from_utf8_lossy(&build.stdout).lines() {
            let Ok(v) = serde_json::from_str::<serde_json::Value>(l) else {
                continue;
            };
            let Some(exe) = v["executable"].as_str() else { continue };
            if v["profile"]["test"].as_bool() != Some(true) {
                continue;
            }
            let id = v["package_id"].as_str().unwrap_or("");
            // `path+file:///…/olv-core#0.1.0`, or `…/dir#olv-core@0.1.0` when the name
            // differs from the folder.
            let (path, tail) = id.rsplit_once('#').unwrap_or((id, ""));
            let krate = match tail.split_once('@') {
                Some((name, _)) => name.to_string(),
                None => path.rsplit('/').next().unwrap_or("").to_string(),
            };
            let target = v["target"]["name"].as_str().unwrap_or("").to_string();
            let dir = Path::new(v["manifest_path"].as_str().unwrap_or("."))
                .parent()
                .map(Path::to_path_buf);
            binaries.push((krate, target, PathBuf::from(exe), dir.unwrap_or_else(rust_dir)));
        }

        // Unit tests are named by module (`base::smoothing::tests::x`). Reference tests
        // live in `tests/oracle_<set>.rs` with bare names, so they count for the package
        // whose `oracle/expected/<package>/<set>.json` they read.
        let line = Regex::new(r"^test ([\w:]+) \.\.\. (ok|FAILED)").unwrap();
        let set_owner: BTreeMap<String, String> = pkgs
            .iter()
            .flat_map(|p| {
                let dir = rust_dir().join("oracle").join("expected").join(&p.id);
                fs::read_dir(dir)
                    .into_iter()
                    .flatten()
                    .flatten()
                    .filter_map(|e| {
                        let name = e.file_name().to_string_lossy().to_string();
                        name.strip_suffix(".json").map(|s| (s.to_string(), p.module.clone()))
                    })
                    .collect::<Vec<_>>()
            })
            .collect();
        for (krate, target, exe, dir) in &binaries {
            let module_override = target
                .strip_prefix("oracle_")
                .and_then(|set| set_owner.get(set))
                .cloned();
            let out = Command::new(exe)
                .current_dir(dir)
                .output()
                .map_err(|e| format!("{}: {e}", exe.display()))?;
            for l in String::from_utf8_lossy(&out.stdout).lines() {
                let Some(c) = line.captures(l) else { continue };
                let module = module_override
                    .clone()
                    .unwrap_or_else(|| c[1].split("::").next().unwrap_or("").to_string());
                let e = results.entry(krate.clone()).or_default().entry(module).or_default();
                if &c[2] == "ok" { e.0 += 1 } else { e.1 += 1 }
            }
        }
    }

    println!(
        "{:<22} {:>2} {:<7} {:>6} {:>8} {:>10}  tests ok/fail",
        "package", "w", "model", "C# ln", "C# tests", "rust"
    );
    for p in &pkgs {
        let cs_lines: usize = p
            .files
            .iter()
            .filter_map(|f| resolve(f))
            .filter_map(|path| inv.sources.get(&rel(&path)))
            .map(|s| s.lines)
            .sum();
        let cs_tests: usize = inv
            .tests
            .iter()
            .filter(|t| test_owner(t, &file_pkg).as_deref() == Some(&p.id))
            .map(|t| t.tests)
            .sum();
        let src = rust_dir().join("crates").join(&p.krate).join("src");
        let rust_lines = [src.join(format!("{}.rs", p.module)), src.join(&p.module)]
            .iter()
            .map(|path| count_rust_lines(path))
            .sum::<usize>();
        let rust = if rust_lines == 0 {
            "-".to_string()
        } else {
            format!("{rust_lines} ln")
        };
        let tests = match results.get(&p.krate).and_then(|m| m.get(&p.module)) {
            Some((ok, fail)) => format!("{ok}/{fail}"),
            None if run => "0/0".into(),
            None => "(not run)".into(),
        };
        println!(
            "{:<22} {:>2} {:<7} {:>6} {:>8} {:>10}  {}",
            p.id, p.wave, p.model, cs_lines, cs_tests, rust, tests
        );
    }
    Ok(ExitCode::SUCCESS)
}

fn count_rust_lines(path: &Path) -> usize {
    if path.is_file() {
        return fs::read_to_string(path)
            .map(|s| s.lines().filter(|l| !l.trim().is_empty()).count())
            .unwrap_or(0);
    }
    let Ok(entries) = fs::read_dir(path) else { return 0 };
    entries.flatten().map(|e| count_rust_lines(&e.path())).sum()
}

#[derive(Deserialize)]
struct CorpusConfig {
    #[serde(default)]
    logs: Vec<String>,
    #[serde(default)]
    tunes: Vec<String>,
    #[serde(default)]
    definitions: Vec<String>,
}

fn corpus() -> Res {
    let cfg_path = rust_dir().join("corpus.toml");
    let text = fs::read_to_string(&cfg_path)
        .map_err(|_| "no rust/corpus.toml; copy corpus.example.toml and edit it".to_string())?;
    let cfg: CorpusConfig = toml::from_str(&text).map_err(|e| e.to_string())?;
    let mut manifest = BTreeMap::new();
    for (kind, patterns) in [
        ("logs", &cfg.logs),
        ("tunes", &cfg.tunes),
        ("definitions", &cfg.definitions),
    ] {
        let dest = rust_dir().join("corpus").join(kind);
        fs::create_dir_all(&dest).map_err(|e| e.to_string())?;
        let mut names = Vec::new();
        for pat in patterns {
            for src in expand(pat) {
                let name = src.file_name().unwrap().to_string_lossy().to_string();
                fs::copy(&src, dest.join(&name)).map_err(|e| format!("{}: {e}", src.display()))?;
                names.push(name);
            }
        }
        names.sort();
        println!("{kind}: {} files", names.len());
        manifest.insert(kind, names);
    }
    fs::write(
        rust_dir().join("corpus").join("manifest.json"),
        serde_json::to_string_pretty(&manifest).unwrap(),
    )
    .map_err(|e| e.to_string())?;
    Ok(ExitCode::SUCCESS)
}

/// A path whose last component may hold one `*`.
fn expand(pattern: &str) -> Vec<PathBuf> {
    let p = Path::new(pattern);
    let name = p
        .file_name()
        .map(|n| n.to_string_lossy().to_string())
        .unwrap_or_default();
    if !name.contains('*') {
        return if p.is_file() { vec![p.to_path_buf()] } else { vec![] };
    }
    let (pre, post) = name.split_once('*').unwrap();
    let Some(dir) = p.parent() else { return vec![] };
    let Ok(entries) = fs::read_dir(dir) else { return vec![] };
    let mut v: Vec<PathBuf> = entries
        .flatten()
        .map(|e| e.path())
        .filter(|f| {
            let n = f.file_name().unwrap().to_string_lossy().to_lowercase();
            f.is_file() && n.starts_with(&pre.to_lowercase()) && n.ends_with(&post.to_lowercase())
        })
        .collect();
    v.sort();
    v
}
