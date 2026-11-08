# OpenLogViewer in Rust: the port plan

Status: **tooling built, 2026-11-07.** Only `UnitConvert` is ported, as the worked example. Every agent working on the port
reads this file and `CONVENTIONS.md` (written in Wave 0) before anything else.

## Goal

Replace the C# application with a Rust one that behaves the same: same logs read the
same way, same tunes written byte-for-byte, same protocols on the wire. Then delete the
C#. The C# app keeps shipping until the Rust one has matched it on every ECU on the
bench (Wave 5). Until then it is the reference that settles disputes: when the two
disagree, the C# is right unless a person decides otherwise.

"Pure Rust" means every line we write is Rust, and no C library is linked: not
FTDI's `ftd2xx`, not libusb. The operating system's own interfaces (Win32, WinRT,
CoreBluetooth, BlueZ, usbfs, IOKit) are reached through Rust bindings to them.

**Windows, Linux and macOS**, all from the first release. The C# app was Windows-only,
so the port adds the other two platforms as it goes, not as a later project.

## Defaults (change any of these before Wave 0 starts)

| Decision | Default | Why |
|---|---|---|
| UI toolkit | **egui / eframe** (decided) | Every heavy view (plot, heat table, gauges, scatter, histogram, curve, tune table) already draws itself, so each maps onto egui's painter almost line for line. Themes, custom fonts and zoom (`zoom_factor`) are built in. |
| Where | `rust/` cargo workspace in **this repo** (decided) | The C# reference and the Rust port sit side by side, so one command can run both on the same input. |
| Platforms | **Windows, Linux, macOS** (decided). Nothing platform-specific above `olv-transport`, and inside it each OS is its own module behind one trait | CI builds and tests all three on every push, so a Windows-only call anywhere else fails the build the day it is written. |
| Licences | `cargo-deny`, allowing MIT/Apache/BSD/Zlib/ISC only | Keeps THIRD-PARTY-NOTICES' "no copyleft code" promise checkable. |
| Retro font | **Our own** (decided): an original 8x16 pixel font drawn from scratch, MIT like the rest, built into a TTF by `olv-fontgen` | Removes the CC BY-SA question entirely. Not traced from IBM VGA or Px437. |
| Number formatting | One `fmt` module that copies .NET's `G5`/`F1`/`N0` output, culture-invariant | The single most likely source of a thousand small mismatches. Built in Wave 0, used everywhere. |

## Crates

```
rust/
  crates/
    olv-core       data model, log readers, INI/.msq read+write, units, math expressions,
                   analysis, calculators. Pure: no I/O beyond files, no threads.
    olv-protocol   every ECU protocol as a sans-IO state machine (bytes in, bytes/events out):
                   MS/Speeduino/rusEFI, ELM327/OBD2, SSM, MaxxECU BT+USB+CAN, Sniper.
    olv-transport  serial (serialport), Wi-Fi TCP, BLE (btleplug), FTDI (our own FTDI
                   protocol over nusb, or its virtual COM port), USB (nusb), port naming
                   (serialport's USB info, all three OSes). The only platform-specific crate.
    olv-live       LiveSession, EcuConnection, recording, sources (ties protocol to transport).
    olv-store      settings, presets, filters, workspace, tuning projects (serde JSON).
    olv-agent      the local agent HTTP/WebSocket API, with write gates and guardrails.
    olv-fontgen    builds the Retro TTF and a preview PNG from an ASCII-art glyph source.
    olv-oracle     test-only: loads the C# reference outputs and compares, with float tolerance.
  apps/
    olv            the egui application.
    olv-mcp        MCP server (rmcp).
    olv-dump, olv-insights, olv-probe   the console tools.
```

Splitting protocol from transport is the most important structural decision here. The
protocols are where the hardware found real bugs, and as pure state machines they can be
tested against recorded wire traces (`WireTrace.cs` already records them) with no
hardware attached.

## How correctness is checked: the oracle

Most of the token saving comes from never having a model judge whether ported code is
right. A program decides.

1. **Wave 0** extends the C# `OpenLogViewer.Dump` tool into a reference generator. For
   every input in the corpus it writes canonical JSON: decoded channels (names, units,
   per-channel min/max/mean, a hash of every sample), INI and .msq parse results, .msq
   write output (bytes), histogram and VE tables, insights, math-expression results,
   calculator outputs, and protocol encode/decode of recorded wire traces.
2. `olv-oracle` loads those files, and every Rust test compares against them. Floats
   match to a stated tolerance; .msq output and wire bytes match exactly.
3. An agent is done when `cargo test` for its package passes, the oracle included.
   Nobody reads the code to decide that.

**Corpus.** The 2,246 C# core tests generate their logs in code, so they port without
fixtures. Real-world inputs (the logs in Downloads, the .msq files, the rusEFI and
Speeduino tunes, the INIs) are personal data, so they stay **local and gitignored** under
`rust/corpus/`. Only the generator and the comparison code are committed.

## Agents, models, and the rules that keep token use down

| Role | Model | Used for | Effort |
|---|---|---|---|
| Orchestrator | Opus (the main session) | Holds this plan, cuts work packages, merges, reads only reports and test results | — |
| Architect | Opus | Wave 0 only: workspace, shared types, `fmt`, error types, `CONVENTIONS.md`. Everything downstream inherits these, so they get the strongest model once. | higher |
| Porter | Sonnet | The bulk: one package of C# files becomes one Rust module, with its C# tests ported alongside | default |
| Mechanical | Haiku | Oracle emitters, translating xunit tests once the Rust API exists, clippy and fmt fixes, cargo-deny config, audits | lower |
| Reviewer | Opus | Only for high-risk code (list below), given the C# source and the Rust diff, never the porter's summary | default |
| UI builder | Sonnet | egui screens built from `UI-SPEC.md` and the reference screenshots | default |

**Rules**

1. **Briefs point, they don't paste.** A brief names this file, `CONVENTIONS.md`, the
   C# files to port and the oracle cases to pass. It contains no code.
2. **Reports are short.** At most 15 lines: files written, tests passing, oracle cases
   passing, open questions. No code, and no restating the brief.
3. **A program accepts the work, not a model.** The gate is `cargo test -p <crate>`
   plus the oracle; the orchestrator reads results, not code.
4. **One worktree per agent, disjoint files.** Packages are cut so that no two agents in
   a wave touch the same file. The orchestrator merges.
5. **Shared types freeze after Wave 0.** A porter that needs a change asks the
   orchestrator; it does not edit `olv-core::model`. Without this rule, agents spend
   tokens reconciling each other's edits.
6. **Escalate, don't repeat.** A package that fails its gate twice moves up a model
   (Haiku → Sonnet → Opus) with the failure output attached, instead of running a third
   time at the same level.
7. **At most 8 agents per wave.**
8. **Review only where a mistake reaches an engine.** Opus reviews these and nothing
   else: protocols, transports, the .msq writer, burn and restore logic,
   `DangerousConstants` and the agent write gates.

## Waves

The packages, their files, models and order live in **`packages.toml`**, and
`cargo xtask check` proves the order against the code: every C# file is in exactly
one package, no package uses code from a later wave, and no two packages in the
same wave need each other. My first hand-drawn order failed that check in 21 places,
so the real dependency layers (`cargo xtask layers`) set the waves, not the tidiness
of the grouping.

| Wave | Packages | What |
|---|---|---|
| 0 | 2 (Opus `w0-model`, Sonnet `w0-base`, in parallel) | data model, units, the interfaces, `fmt`, `TuningMath`, `MathExpression`, `Smoothing`, JSON settings files |
| 1 | 7 | calculators, channels, INI parsing and tune layout, MS and MaxxECU protocols, Sniper, settings stores |
| 2 | 7 | log readers, math channels, OBD2/SSM, **transports**, definitions, filters, manifold calculator |
| 3 | 2 | ECU connections, analysis |
| 4 | 3 | tune operations (including the .msq writer), insights, live sessions |
| 5 | 2 | agent API and tools, tuning projects |
| 6 | 2 | interface spec (Opus) and UI base: themes, colours, title bar |
| 7 | 5 | screens: plot, histogram and scatter, calibration, gauges and faults, calculators |
| 8 | 1 | application state (MainViewModel, 5,300 lines; the largest single package) |
| 9 | 1 | the shell: App, MainWindow and InsightsWindow, which use each other |
| H | — | hardware, by the user and the orchestrator (below) |
| C | — | cut-over |

32 packages: 27 Sonnet, 3 Haiku, 2 Opus, plus 9 Opus reviews (every package marked
`review = true`). All 2,067 C# test methods are assigned to a package.

### Gates per wave

- **Every wave:** each package's `cargo test`, clippy and fmt pass, reference cases included.
- **Waves 1 to 5:** packages with `review = true` get an Opus review of the diff.
- **Wave 2 onward:** protocol and connection packages also replay recorded wire
  traces byte-exact through a fake transport.
- **Wave 6:** the spec is read and agreed by the user before Wave 7 starts.
- **Waves 7 to 9:** the user compares each screen against its reference screenshot.
  Pixel diffs aren't possible across toolkits.

### Hardware (H)

Same checks that proved the C# app, from the hardware notes:

| Device | Port | What must be proven |
|---|---|---|
| Speeduino (bench) | COM14 | read, write, burn, restore. Opening the port resets the board, so unburned changes are lost. |
| rusEFI uaEFI | COM8 | read, write, burn across a reboot (status 0x04 = burn OK) |
| MicroSquirt (in a live car) | COM3 | **read-only** unless the user says otherwise |
| MaxxECU Race | USB/FTDI | live data, writes (no separate burn step), needs 12 V, MTune closed |
| OBD2 dongle | Bluetooth LE | trims, MAP, lambda on a running engine |
| Vgate Wi-Fi | TCP | still never tested in either app |
| Holley Sniper | WinUSB | blocked at layer 3 in both apps until a USB capture exists |

**Gate:** every row that passes in C# today passes in Rust. Then build the installer
(cargo-wix), with code signing still to be decided.

### Cut-over (C)

Delete the C# sources, the reference generator, and the comparison code that only
existed to compare. Update README, CHANGELOG and THIRD-PARTY-NOTICES.

## Tooling

| Command | What it saves |
|---|---|
| `cargo xtask check` | Finding out mid-wave that an agent needs code nobody has ported yet |
| `cargo xtask layers` | Working out the dependency order by reading 174 files |
| `cargo xtask brief <id>` | Writing a brief: files, sizes, already-ported dependencies by module, the tests to port, the gate |
| `cargo xtask status [--run]` | Reading test output: one line per package, C# vs Rust lines, tests passing |
| `cargo xtask inventory` | Finding every .NET format string by hand (34 of them); writes `oracle/formats.txt` |
| `cargo xtask corpus` | Gathering the real logs, tunes and INIs (local only, gitignored) |
| `dotnet run --project tools/OpenLogViewer.Oracle` | Deciding by reading whether a port is right: the C#'s answers, committed |

CI (`.github/workflows/rust.yml`) runs fmt, clippy, tests and `xtask check` on
Windows and Linux, `cargo deny` for licences and advisories, and regenerates the
synthetic references from the C# so they cannot drift from what it computes.

## Known risks

- **Float and text formatting.** .NET and Rust print numbers differently by default.
  The `fmt` module and the oracle exist mainly because of this.
- **Bluetooth LE.** `btleplug` (Windows, macOS, Linux) is less proven than the WinRT
  calls in use today. The fallback per OS is the platform API through its Rust bindings.
- **FTDI without FTDI's library.** On Linux (detach `ftdi_sio`) and macOS, the chip is
  driven over raw USB with our own implementation of its control requests. On Windows,
  FTDI's driver owns the device, so the pure-Rust path is the virtual COM port it
  exposes; whether MaxxECU's driver setup exposes one is a hardware question. If it
  doesn't, Windows MaxxECU-over-USB is the one place that would still need FTDI's DLL.
- **Three platforms, one bench.** Hardware checks were all done on Windows. Linux and
  macOS need at least the serial ECUs and the OBD2 dongle re-proved on each.
- **Plot performance.** egui redraws every frame, so the plot needs the same min/max
  decimation the C# plot uses, cached between frames.
- **The interface is a redesign.** Some WPF behaviours (focus, keyboard navigation,
  text input) will differ. The spec has to name the ones that matter for tuning,
  such as keys on the tune table.
- **Hardware time.** Wave 5 is limited by bench access, not effort.

## Decided (2026-11-07)

egui; this repo under `rust/`; Windows, Linux and macOS from the first release; an
original MIT pixel font instead of Px437; work on branch `rust-port`.
