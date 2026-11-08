# Conventions for porting agents

Read this and `PLAN.md` before your brief. Your brief (from `cargo xtask brief <id>`)
says which files to port; this says how. **Wave 0 (`w0-model`) owned this file**;
the sections it decided are marked *(Wave 0)*.

## What you may touch

- Only the module your brief names, `rust/crates/<crate>/src/<module>`, its tests
  under `rust/crates/<crate>/tests/`, and the `pub mod` line in that crate's `lib.rs`.
- Not `olv-core::model` or any other package's module. If you need a shared type
  changed, say so in your report and work around it. Two agents editing one
  type cost more than the workaround.
- Not `rust/oracle/expected/`. Those files are the C#'s answers. If one looks wrong,
  report it; never make the Rust disagree with the C# on purpose.
- A new crate dependency only through `[workspace.dependencies]` in `rust/Cargo.toml`,
  and only with a licence `deny.toml` allows. Name it in your report.

## Porting

- **Bugs are fixed, not ported** (user decision, 2026-11-08). Anything listed in `CSHARP-DEFECTS.md`, or marked as a bug in `UI-SPEC.md`, is fixed in the Rust. The fix needs a test that would fail on the C#'s behaviour, and the entry in `CSHARP-DEFECTS.md` is updated to say "fixed in Rust". A newly found bug is fixed the same way and added. Everything else follows the rule below.
- **Behaviour is the C#'s, exactly.** Same branches, same order of floating-point
  operations (`x * 9.0 / 5.0` is not `x * 1.8`; the reference test compares bits and
  will catch it), same edge cases. Improve nothing while porting; note it in the report.
- One C# file becomes one Rust file named in snake_case (`MlgReader.cs` →
  `mlg_reader.rs`), inside your module directory.
- The C# comments explain *why*. Keep the ones that explain behaviour as rustdoc;
  drop the ones that only describe WPF or C# mechanics.
- Samples are `f32` (C# `float[]`), arithmetic is `f64`. Do not widen storage.
- **Numbers to text: `olv_core::fmt`** *(Wave 0)*, never `format!("{:.1}")` for a
  number the C# formats (`"F1"`, `"0.#"`, `{x}`): .NET rounds differently, and
  `fmt` matches it exactly (oracle: 3,724 cases; fuzzed against .NET on 760k more).
  Pick the culture the C# used:
  - **en-US** where the C# passed no provider or `CurrentCulture` (interpolation,
    `ToString("F1")`, `double.TryParse(s, out v)`): `fmt::f64(v, "F1")`,
    `fmt::f32`, `fmt::int` (C# `int`), `fmt::long` (C# `long`), `fmt::parse_f64`.
  - **invariant** only where it passed `CultureInfo.InvariantCulture`:
    `fmt::f64_inv`, `f32_inv`, `int_inv`, `long_inv`, `parse_f64_inv`.
  - They differ: en-US `P0` is `67%` (invariant `67 %`), a bare `F`/`N`/`P` has 3
    decimals (invariant 2), and infinity is `∞`/`-∞` (invariant `Infinity`).
  - `""` is the default `ToString()`. A C# `float` uses `fmt::f32` (its shortest
    text is the float's). `try_f64(v, fmt, Culture)` etc. return
    `Error::Format` where .NET throws; the plain forms panic there. Integers:
    `fmt::parse_i32`/`parse_i64`; hex bytes `parse_hex_u8`.
- Errors *(Wave 0)*: `olv_core::Result<T>` = `Result<T, olv_core::Error>`, never a
  panic on bad input from a file or an ECU. One variant per C# exception:
  `IOException`→`Io`, `LogFormatException`→`LogFormat`, `FormatException`→`Format`,
  `XmlException`→`Xml`, `JsonException`→`Json`, `MathExpressionException`→
  `Expression{message, position}`, `EcuProtocolException`→`Protocol`,
  `TimeoutException`→`Timeout`, `Argument*Exception`→`Argument`,
  `InvalidOperationException`→`InvalidOperation`. Keep the C#'s message text. Other
  crates use this type (it is `#[non_exhaustive]`; ask for a new variant). A file
  the C# refuses must be refused here too.
- Model *(Wave 0)*, in `olv_core::model`:
  - C# interfaces are traits (`LogReader: Send + Sync`, `LiveSource: Send`,
    `CanSource: Send`, `RawTelemetrySource`). `IDisposable` is `Drop`. The C#'s
    `source as IRawTelemetrySource` is `LiveSource::raw_telemetry()`.
  - C# default parameters become explicit arguments; a C# method with more than
    seven parameters takes a struct with `Default` (see `WireAttempt`).
  - `int` is `i32` and `long` `i64`, but counts and indexes are `usize`, and a
    `-1` "none" index is `Option<usize>` (`LogChannel::min_index`). Where the C#
    indexes with a value that may be negative, use `LogChannel::at_signed`.
  - Shared C# objects are `Arc<T>` (`LogDocument` channels); `ReferenceEquals` is
    `Arc::ptr_eq`/`std::ptr::eq`. `init`-only records are private fields with
    getters and `with_` builders.
  - `Dictionary<string,T>(StringComparer.OrdinalIgnoreCase)` is
    `model::IgnoreCaseMap` (insertion order, first key casing kept);
    `string.Equals(.., OrdinalIgnoreCase)` is `model::eq_ignore_case`. `T[,]` is
    `model::Array2<T>`, indexed `[(i, j)]`.
  - XML: `model::safe_xml::parse` (never `roxmltree` directly), an owned read-only
    tree with `descendants()`, `local_name()`, `attribute()`, `value()`.
  - Text files: `model::tuning_text::read`. Strings the C# measures with
    `.Length` count UTF-16 units (`s.encode_utf16().count()`).
- **Three platforms.** Everything must build and pass on Windows, Linux and macOS. No
  `#[cfg(windows)]`, Win32 paths, `\` separators, `COMx` assumptions or registry use
  outside `olv-transport`; use `std::path` and `std::env` for paths and folders. Where
  the C# reads a Windows location (`%APPDATA%`), port the intent with a per-OS folder
  (the `directories` crate) and say so in the report.
- No `unsafe`; the workspace denies it. The one exception is `olv-transport`'s per-OS
  modules, where an operating-system call through its Rust bindings needs it; there
  it is allowed per module and reviewed. No third-party C library is ever linked (no libusb, no FTDI `ftd2xx`); the operating system's own libraries and frameworks (Win32, IOKit, CoreFoundation, BlueZ over D-Bus) are the OS interface and are fine. The single exception is FTDI's `ftd2xx.dll`, loaded at runtime on Windows for MaxxECU USB only (see PLAN.md); nothing else may load a vendor library.

## Tests

- Port every C# test your brief lists as a `#[test]` with the same name in
  snake_case, next to the code (`#[cfg(test)] mod tests`). Keep the comment that
  says why the test exists.
- Reference tests go in `rust/crates/<crate>/tests/oracle_<set>.rs`. Copy the
  pattern in `olv-core/tests/oracle_units.rs`: deserialize the cases with
  `olv_oracle::cases(package, set)`, run the Rust, compare. Doubles arrive as
  IEEE bits (`f64::from_bits`), so compare bits where the C# does the same
  arithmetic, and `olv_oracle::assert_close` only where it cannot (accumulated sums).
- Corpus cases (real logs and tunes) come through `olv_oracle::corpus_cases`, which
  returns `None` on machines without the corpus. Return early then; CI has none.
- Your package needs reference outputs that don't exist yet? Add a class under
  `tools/OpenLogViewer.Oracle/Emitters/` named after your package (copy
  `W0Units.cs`), run `dotnet run --project tools/OpenLogViewer.Oracle -- --only <id>`,
  and commit what it writes. Never hand-write expected outputs.

## Gate

```
cd rust
cargo test -p <crate>
cargo clippy -p <crate> --all-targets -- -D warnings
cargo fmt --all --check
```

All three pass, or you are not done.

## Report

Fifteen lines at most, no code:

1. Files written.
2. Tests: ported / passing / failing; reference cases passing.
3. Anything not ported, and why.
4. Anything in the C# that looks like a bug (ported as-is, not fixed).
5. Shared-type changes you need.
