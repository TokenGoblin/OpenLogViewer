//! `olv-insights`: what a datalog says about the engine that produced it, without the window.
//!
//! Ported from `tools/OpenLogViewer.Insights/Program.cs`. Everything the Insights pane says about
//! a log, from a command line: no display, no ECU, nothing to click. That makes the analysis
//! usable in the places it is most wanted and least available: over a folder of logs at once,
//! from a script after a drive, and by an assistant that has a file and no way to open a program.
//!
//! The output and the exit code are the C#'s, because scripts branch on them: 0 nothing wrong,
//! 1 something worth watching, 2 a warning (or a log that could not be read).
//!
//! **Not ported yet: `--project`.** Recording what was found into a vehicle's project needs the
//! tuning-project store, which another package owns and which is not merged. The option is still
//! read, so a script that passes it gets its findings and its exit code, and a message on stderr
//! saying nothing was recorded (and exit code 2, so it is not mistaken for a success).

use std::path::{Path, PathBuf};
use std::process::ExitCode;

use olv_core::fmt;
use olv_core::insights::log_insights::{self, InsightLevel, LogInsight};
use olv_core::logs::log_reader_factory;
use olv_core::model::LogDocument;

/// Switches that swallow the token after them.
const TAKES_A_VALUE: [&str; 3] = ["--project", "--projects", "--note"];

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();

    if args.is_empty() || args.iter().any(|a| a == "--help" || a == "-h") {
        usage();
        return if args.is_empty() {
            ExitCode::from(2)
        } else {
            ExitCode::SUCCESS
        };
    }

    let vehicle = value(&args, "--project");
    let as_json = args.iter().any(|a| a == "--json");
    let quiet = args.iter().any(|a| a == "--quiet");

    let logs = expand(&loose(&args));

    if logs.is_empty() {
        eprintln!("No logs given. Pass files, or a folder to read every log in it.");
        return ExitCode::from(2);
    }

    let mut worst = 0;

    for path in &logs {
        match log_reader_factory::load(Path::new(path)) {
            Ok(log) => {
                let found = log_insights::from(&log);

                if !quiet {
                    if as_json {
                        println!("{}", json(path, &log, &found));
                    } else {
                        print(path, &log, &found, logs.len() > 1);
                    }
                }

                // The exit code is the worst thing found, so a script can act on it without
                // parsing anything: 0 nothing wrong, 1 something to watch, 2 a warning.
                worst = worst.max(severity(&found));
            }
            Err(e) => {
                eprintln!("{}: {e}", file_name(path));
                worst = worst.max(2);
            }
        }
    }

    if let Some(vehicle) = vehicle {
        eprintln!(
            "olv-insights: recording into the project \"{vehicle}\" needs the tuning-project store, \
             which is not part of this build yet. Nothing was recorded."
        );
        worst = worst.max(2);
    }

    ExitCode::from(worst)
}

/// `Path.GetFileName`.
fn file_name(path: &str) -> String {
    path.rsplit(['/', '\\']).next().unwrap_or(path).to_string()
}

/// The arguments that are logs: everything that is neither a switch nor the value of one.
///
/// Taking every token without a leading dash is not enough, and it showed: `--note "baseline
/// before any changes"` was read as a request to analyse a log of that name, which then reported
/// "Log file not found" and set the exit code to 2 on a run that had otherwise gone perfectly.
fn loose(args: &[String]) -> Vec<String> {
    let mut found = Vec::new();
    let mut i = 0;

    while i < args.len() {
        if args[i].starts_with('-') {
            if TAKES_A_VALUE.iter().any(|t| t.eq_ignore_ascii_case(&args[i])) {
                i += 1;
            }
        } else {
            found.push(args[i].clone());
        }

        i += 1;
    }

    found
}

/// Files as given, and every log inside any folder given.
fn expand(paths: &[String]) -> Vec<String> {
    let mut all = Vec::new();

    for path in paths {
        let folder = PathBuf::from(path);

        if folder.is_dir() {
            let filter = log_reader_factory::OPEN_FILE_FILTER.to_lowercase();
            let mut found: Vec<String> = std::fs::read_dir(&folder)
                .map(|entries| {
                    entries
                        .filter_map(Result::ok)
                        .filter(|e| e.path().is_file())
                        .map(|e| e.path().to_string_lossy().into_owned())
                        // `Path.GetExtension` keeps the dot and is empty for no extension, and
                        // the C# asks whether the *filter text* contains it, so a file with no
                        // extension, or one that is a fragment of a listed one (".c"), is in.
                        .filter(|f| filter.contains(&extension(f).to_lowercase()))
                        .collect()
                })
                .unwrap_or_default();

            // `StringComparer.OrdinalIgnoreCase`.
            found.sort_by_key(|a| a.to_uppercase());
            all.extend(found);
            continue;
        }

        all.push(path.clone());
    }

    all
}

/// `Path.GetExtension`: the last dot of the file name and what follows it, or nothing.
fn extension(path: &str) -> String {
    let name = file_name(path);

    match name.rfind('.') {
        Some(dot) if dot + 1 < name.len() => name[dot..].to_string(),
        _ => String::new(),
    }
}

/// The worst level found, as an exit code a script can branch on.
fn severity(found: &[LogInsight]) -> u8 {
    if found.iter().any(|f| f.level == InsightLevel::Warning) {
        return 2;
    }
    if found.iter().any(|f| f.level == InsightLevel::Watch) {
        return 1;
    }

    0
}

fn order(level: InsightLevel) -> u8 {
    match level {
        InsightLevel::Warning => 0,
        InsightLevel::Watch => 1,
        InsightLevel::Note => 2,
        InsightLevel::Good => 3,
        _ => 4,
    }
}

fn seconds(log: &LogDocument) -> f64 {
    let n = log.time().len();

    if n > 0 { log.time().at(n - 1) } else { 0.0 }
}

fn print(path: &str, log: &LogDocument, found: &[LogInsight], many: bool) {
    if many {
        println!();
    }

    let name = file_name(path);
    println!("{name}");
    println!("{}", "-".repeat(name.encode_utf16().count()));
    println!(
        "{} samples over {} s, {} channels",
        fmt::int(log.time().len() as i32, "N0"),
        fmt::f64(seconds(log), "N0"),
        log.channels().len()
    );
    println!();

    // Worst first. Somebody reading a wall of output stops early, and what they stop before
    // should be the part that did not need them.
    let mut sorted: Vec<&LogInsight> = found.iter().collect();
    sorted.sort_by_key(|f| order(f.level));

    for insight in sorted {
        println!("[{:<11}] {}", insight.level.name(), insight.topic);
        println!("  {}", insight.title);

        if !insight.detail.is_empty() {
            println!("  {}", insight.detail);
        }
        if !insight.evidence.is_empty() {
            println!("  {}", insight.evidence);
        }

        println!();
    }
}

/// `JsonSerializer.Serialize` with indentation and relaxed escaping, for the one shape this
/// prints.
fn json(path: &str, log: &LogDocument, found: &[LogInsight]) -> String {
    let mut out = String::new();

    out.push_str("{\n");
    out.push_str(&format!("  \"log\": {},\n", quote(&file_name(path))));
    out.push_str(&format!("  \"samples\": {},\n", log.time().len()));
    out.push_str(&format!("  \"seconds\": {},\n", number(seconds(log))));
    out.push_str(&format!("  \"channels\": {},\n", log.channels().len()));

    if found.is_empty() {
        out.push_str("  \"findings\": []\n");
    } else {
        out.push_str("  \"findings\": [\n");

        for (i, f) in found.iter().enumerate() {
            out.push_str("    {\n");
            out.push_str(&format!("      \"level\": {},\n", quote(f.level.name())));
            out.push_str(&format!("      \"topic\": {},\n", quote(&f.topic)));
            out.push_str(&format!("      \"title\": {},\n", quote(&f.title)));
            out.push_str(&format!("      \"detail\": {},\n", quote(&f.detail)));
            out.push_str(&format!("      \"evidence\": {}\n", quote(&f.evidence)));
            out.push_str(if i + 1 < found.len() { "    },\n" } else { "    }\n" });
        }

        out.push_str("  ]\n");
    }

    out.push('}');
    out
}

/// A double as `System.Text.Json` writes it (non-finite ones as their names).
fn number(v: f64) -> String {
    if v.is_finite() {
        fmt::f64_inv(v, "")
    } else {
        format!("\"{}\"", fmt::f64_inv(v, ""))
    }
}

/// A string with the relaxed escaping: only quotes, backslashes and control characters.
fn quote(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 2);
    out.push('"');

    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            '\u{8}' => out.push_str("\\b"),
            '\u{c}' => out.push_str("\\f"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04X}", c as u32)),
            c => out.push(c),
        }
    }

    out.push('"');
    out
}

/// The token after a switch, unless it is itself a switch.
fn value(args: &[String], name: &str) -> Option<String> {
    let at = args.iter().position(|a| a == name)?;

    args.get(at + 1).filter(|next| !next.starts_with("--")).cloned()
}

fn usage() {
    println!(
        "\
olv-insights \u{2014} what a datalog says about the engine that produced it.

  olv-insights <log|folder> [more\u{2026}] [options]

Reads MLG, MSL and CSV logs. Needs no ECU, no display and no network.

Options
  --project <vehicle>   also record what was found into that vehicle's
                        project: keeps the findings, raises a fix for
                        anything newly warned about, and notes a repeat
                        against the fix already tracking it
  --note <text>         what this sitting was, kept with it
  --projects <folder>   where projects live (default: the workspace)
  --json                machine-readable output
  --quiet               say nothing; use the exit code

Exit code
  0  nothing wrong        1  something worth watching        2  a warning

Examples
  olv-insights drive.mlg
  olv-insights ~/OpenLogViewer/Logs --project \"The E28\" --note \"after VE +4%\"
  olv-insights drive.mlg --json | jq '.findings[] | select(.level==\"Warning\")'"
    );
}
