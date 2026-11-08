# Conventions for porting agents

Read this and `PLAN.md` before your brief. Your brief (from `cargo xtask brief <id>`)
says which files to port; this says how. **Wave 0 (`w0-model`) owns this file** and
fills in the sections marked *(Wave 0)*. Until then they are defaults.

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

- **Behaviour is the C#'s, exactly.** Same branches, same order of floating-point
  operations (`x * 9.0 / 5.0` is not `x * 1.8`; the reference test compares bits and
  will catch it), same edge cases. Improve nothing while porting; note it in the report.
- One C# file becomes one Rust file named in snake_case (`MlgReader.cs` →
  `mlg_reader.rs`), inside your module directory.
- The C# comments explain *why*. Keep the ones that explain behaviour as rustdoc;
  drop the ones that only describe WPF or C# mechanics.
- Samples are `f32` (C# `float[]`), arithmetic is `f64`. Do not widen storage.
- Text a user reads, or that is compared against the C#, goes through `olv_core::fmt`
  *(Wave 0)*, which reproduces .NET's format strings. Never `format!("{:.1}")` a
  number the C# formats with `"F1"` or `"0.#"`: they round differently.
- Culture-invariant everywhere. The C# sometimes uses the current culture; on the
  machines this was proven on, that matched invariant output for every format used.
- C# PascalCase methods and properties become snake_case functions or fields; types
  keep their names. Static classes become modules of free functions.
- Errors: *(Wave 0 decides; default)* `Result<T, olv_core::Error>`, never panic on
  bad input from a file or an ECU. A file the C# refuses must be refused here too.
- **Three platforms.** Everything must build and pass on Windows, Linux and macOS. No
  `#[cfg(windows)]`, Win32 paths, `\` separators, `COMx` assumptions or registry use
  outside `olv-transport`; use `std::path` and `std::env` for paths and folders. Where
  the C# reads a Windows location (`%APPDATA%`), port the intent with a per-OS folder
  (the `directories` crate) and say so in the report.
- No `unsafe`; the workspace denies it. The one exception is `olv-transport`'s per-OS
  modules, where an operating-system call through its Rust bindings needs it; there
  it is allowed per module and reviewed. No C library is ever linked.

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
