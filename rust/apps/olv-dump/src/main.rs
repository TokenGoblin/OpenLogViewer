//! `olv-dump`: decodes a log and prints a summary.
//!
//! Ported from `tools/OpenLogViewer.Dump/Program.cs`. A console companion to the viewer that
//! doubles as the regression check for the readers. The output format is the C#'s, line for line
//! and column for column, because scripts parse it.
//!
//! ```text
//! olv-dump <logfile> [more logs...] [--channels] [--categories] [--tune]
//! ```

use std::cmp::Ordering;
use std::path::Path;
use std::process::ExitCode;
use std::time::Instant;

use olv_core::channels::channel_classifier::{self, ChannelCategory};
use olv_core::fmt;
use olv_core::logs::log_reader_factory;
use olv_core::model::tune_axes;
use olv_core::model::{LogChannel, LogDocument};
use olv_core::units::UnitSystem;

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();

    if args.is_empty() {
        eprintln!("usage: olv-dump <logfile> [more logs...]  [--channels]");
        return ExitCode::from(1);
    }

    let list_channels = args.iter().any(|a| a == "--channels");
    let list_categories = args.iter().any(|a| a == "--categories");
    let list_tune = args.iter().any(|a| a == "--tune");
    let paths: Vec<&String> = args.iter().filter(|a| !a.starts_with("--")).collect();
    let mut failures = 0;

    for path in paths {
        let name = file_name(path);

        let started = Instant::now();
        let loaded = log_reader_factory::load(Path::new(path));
        let elapsed = started.elapsed();

        match loaded {
            Ok(log) => describe(
                &name,
                &log,
                elapsed.as_secs_f64() * 1000.0,
                &Listing {
                    channels: list_channels,
                    categories: list_categories,
                    tune: list_tune,
                },
            ),
            Err(e) => {
                failures += 1;
                eprintln!("\n=== {name} ===\n  FAILED: {e}");
            }
        }
    }

    if failures == 0 {
        ExitCode::SUCCESS
    } else {
        ExitCode::from(1)
    }
}

/// Which optional sections to print.
struct Listing {
    channels: bool,
    categories: bool,
    tune: bool,
}

/// `Path.GetFileName`.
fn file_name(path: &str) -> String {
    path.rsplit(['/', '\\']).next().unwrap_or(path).to_string()
}

fn describe(name: &str, log: &LogDocument, decoded_ms: f64, listing: &Listing) {
    println!("\n=== {name} ===");
    println!("  format    : {}", log.format_name());
    println!("  channels  : {}", log.channels().len());
    println!("  samples   : {}", fmt::int(log.sample_count() as i32, "N0"));
    println!(
        "  duration  : {} s  ({} base)",
        fmt::f64(log.duration(), "F2"),
        log.time().name()
    );
    println!("  decoded in: {} ms", fmt::f64(decoded_ms, "F0"));

    if let Some(signature) = log.signature().filter(|s| !s.is_empty()) {
        println!("  signature : {signature}");
    }
    if let Some(info) = log.capture_info().filter(|s| !s.is_empty()) {
        println!("  capture   : {info}");
    }
    if let Some(at) = log.recorded_at() {
        println!("  recorded  : {}", stamp(at));
    }

    let flat = log.channels().iter().filter(|c| c.is_flat()).count();
    println!("  flat/unused channels: {flat}");

    if !log.markers().is_empty() {
        println!("  markers   : {}", log.markers().len());
        for marker in log.markers().iter().take(3) {
            println!("     @{:>8}s  {}", fmt::f64(marker.time, "F2"), marker.text);
        }
    }

    if listing.tune {
        println!();

        match log.embedded_tune().filter(|t| !t.is_empty()) {
            None => println!("  no tune embedded in this log"),
            Some(embedded) => {
                println!(
                    "  embedded tune: {} chars",
                    fmt::int(embedded.encode_utf16().count() as i32, "N0")
                );

                let sets = tune_axes::read_axis_sets(Some(embedded));
                if sets.is_empty() {
                    println!("  no usable table axes found");
                }

                for set in &sets {
                    println!("  {}", set.label());
                    println!(
                        "      {} [{}]: {}",
                        set.x.constant,
                        set.x.units,
                        breakpoints(&set.x.breakpoints)
                    );
                    println!(
                        "      {} [{}]: {}",
                        set.y.constant,
                        set.y.units,
                        breakpoints(&set.y.breakpoints)
                    );
                }
            }
        }
    }

    if listing.categories {
        println!();
        print_categories(log);
    }

    if listing.channels {
        println!();
        println!("  {:<24}{:<8}{:>14}{:>14}", "channel", "units", "min", "max");

        for c in log.channels() {
            println!(
                "  {:<24}{:<8}{:>14}{:>14}",
                trim(c.name(), 23),
                trim(c.units(), 7),
                shown(c, c.min()),
                shown(c, c.max())
            );
        }
    } else {
        for wanted in ["RPM", "MAP", "TPS", "AFR", "CLT", "MAT", "Batt V"] {
            if let Some(c) = log.find_channel(wanted) {
                println!(
                    "    {:<10}{:<6} {:>10} .. {:<10}",
                    c.name(),
                    c.units(),
                    shown(c, c.min()),
                    shown(c, c.max())
                );
            }
        }
    }
}

fn print_categories(log: &LogDocument) {
    let mut groups: Vec<(ChannelCategory, Vec<String>)> = Vec::new();

    for c in log.channels() {
        let category = channel_classifier::classify(c.name(), c.units());

        match groups.iter_mut().find(|(k, _)| *k == category) {
            Some((_, names)) => names.push(c.name().to_string()),
            None => groups.push((category, vec![c.name().to_string()])),
        }
    }

    groups.sort_by_key(|(category, _)| *category);

    for (category, mut names) in groups {
        println!("  {} ({})", channel_classifier::display_name(category), names.len());

        names.sort_by(|a, b| culture_order(a, b));

        for chunk in names.chunks(4) {
            println!("      {}", chunk.join(", "));
        }
    }
}

/// How `string.Order()` sorts in the en-US culture, near enough: letters without regard to case
/// first, then a lower-case letter before its upper-case twin. (The C# uses the current culture's
/// comparer; the exact collation of punctuation is the operating system's and is not copied.)
fn culture_order(a: &str, b: &str) -> Ordering {
    let fold = |s: &str| s.to_lowercase();

    fold(a).cmp(&fold(b)).then_with(|| b.cmp(a))
}

fn shown(channel: &LogChannel, value: f64) -> String {
    channel.format(value, UnitSystem::AsReported)
}

fn breakpoints(values: &[f64]) -> String {
    values.iter().map(|v| fmt::f64(*v, "")).collect::<Vec<_>>().join(" ")
}

/// `s[..n]`, counted in UTF-16 units as the C# counts.
fn trim(s: &str, n: usize) -> String {
    if s.encode_utf16().count() <= n {
        return s.to_string();
    }

    let mut out = String::new();
    let mut units = 0;

    for c in s.chars() {
        units += c.len_utf16();
        if units > n {
            break;
        }
        out.push(c);
    }

    out
}

/// `yyyy-MM-dd HH:mm:ss`.
///
/// In local time, as the C# prints it: a log's recording time is its file's, and a person reading
/// the summary thinks in the clock on their wall. Where the system will not say what zone that is,
/// UTC.
fn stamp(at: std::time::SystemTime) -> String {
    let utc = time::OffsetDateTime::from(at);
    let t = time::UtcOffset::local_offset_at(utc).map_or(utc, |offset| utc.to_offset(offset));

    format!(
        "{:04}-{:02}-{:02} {:02}:{:02}:{:02}",
        t.year(),
        u8::from(t.month()),
        t.day(),
        t.hour(),
        t.minute(),
        t.second()
    )
}
