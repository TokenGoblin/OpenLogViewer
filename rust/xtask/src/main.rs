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
    for (file, pkg) in file_pkg {
        let fstem = Path::new(file)
            .file_name()?
            .to_string_lossy()
            .replace(".xaml.cs", "")
            .replace(".cs", "");
        if fstem == stem {
            return Some(pkg.clone());
        }
    }
    // The interface tests exercise the view model through core types; what they
    // test is the interface, so only interface files can own them.
    let ui_test = t.path.contains("App.Tests");
    let covers: Vec<&String> = t
        .covers
        .iter()
        .filter(|f| !ui_test || f.contains("OpenLogViewer.App/"))
        .collect();
    let mut tally: BTreeMap<&String, usize> = BTreeMap::new();
    for (rank, f) in covers.iter().enumerate() {
        if let Some(pkg) = file_pkg.get(*f) {
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
        let out = Command::new("cargo")
            .args([
                "test",
                "--workspace",
                "--no-fail-fast",
                "--quiet",
                "--",
                "--format=terse",
            ])
            .current_dir(rust_dir())
            .output()
            .map_err(|e| e.to_string())?;
        let text = format!(
            "{}{}",
            String::from_utf8_lossy(&out.stdout),
            String::from_utf8_lossy(&out.stderr)
        );
        let running = Regex::new(r"Running .*deps[\\/]([a-z_]+)-[0-9a-f]+").unwrap();
        let line = Regex::new(r"^test ([\w:]+) \.\.\. (ok|FAILED)").unwrap();
        let mut current = String::new();
        for l in text.lines() {
            if let Some(c) = running.captures(l) {
                current = c[1].replace('_', "-");
            } else if let Some(c) = line.captures(l) {
                let module = c[1].split("::").next().unwrap_or("").to_string();
                let e = results.entry(current.clone()).or_default().entry(module).or_default();
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
