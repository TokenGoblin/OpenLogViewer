<p align="center">
  <img src="docs/logo.png" alt="OpenLogViewer" width="440">
</p>

<h1 align="center">OpenLogViewer</h1>

<p align="center">
  A native Windows datalog viewer and live tuning tool for MegaSquirt, rusEFI,
  Speeduino, MaxxECU and any OBD2 vehicle.<br>
  Built on .NET 10 and WPF.
</p>

<p align="center">
  <img src="docs/mark.png" alt="" width="64">
</p>

---

Open a `.mlg`, `.msl` or `.csv` log, pick channels, and scrub through them. Or
plug into a running engine and read it live.

![OpenLogViewer](docs/screenshot.png)

## What it does

**Reads logs.** MegaSquirt and TunerStudio `.mlg` and `.msl`, rusEFI, MaxxECU,
and delimited text from MoTeC, Haltech, Link, AEM, ECUMaster, Holley, HP Tuners,
Speeduino and most anything else that exports CSV. Nothing is assumed from the
file extension — the content is examined instead.

**Reads them three ways.** Plotted against time; binned into a heat table shaped
like the one in your ECU; or as a scatter, where the spread a table averages away
is still visible. Click a table cell to trace it back to the moment in the log
that produced it, and mark a span in the log to ring the cells it passed through.

**Connects live.** To a MegaSquirt, MicroSquirt, rusEFI, Speeduino or MaxxECU
over a tuning cable, to any OBD2 vehicle through a cheap ELM327 adapter — USB,
Bluetooth LE or Wi-Fi — and to a Subaru over SSM. A live session is an ordinary
log: every filter, preset and analysis works on it unchanged.

**Edits the tune.** Tables and settings, read off the controller rather than from
a file, changed, sent, and burned — each behind its own confirmation that says
what is about to happen.

**Suggests a fuel table.** VE calibration compares logged mixture against the
mixture the tune was asking for, works the wideband's own lag out of the log, and
says which cells it does not have the evidence to move.

## Features

| | |
| --- | --- |
| **Log formats** | Binary `.mlg` including packed flag bytes and interleaved markers; `.msl`; MaxxECU zipped logs; auto-detecting delimited text (delimiter, encoding, units row, decimal comma, time base) |
| **Channel sidebar** | Search, grouping by system, hide-unused, per-channel range, live value at the cursor, jump to a channel's extremes |
| **Presets** | Named channel selections, matched by name so they carry between logs and to a live session |
| **Plot** | Overlaid or stacked, scroll-to-zoom, shift-drag to mark a span, gaps drawn as gaps, steady channels drawn as steady |
| **Per-channel appearance** | Pin a colour or a scale, or smooth a noisy trace — a median, and drawing only, so every measurement still reads the channel as logged |
| **Heat table** | Up to 40 × 40, binned onto the tune's own axes, filtered, traced back to the log in both directions |
| **Scatter** | Every sample at its own X and Y, aggregated onto the display grid so no mark is an accident of draw order |
| **Search** | `Ctrl+F` — `RPM > 4500 && TPS > 80`. Consecutive matches are one finding, not fifty |
| **Calculated channels** | Full expression language; the same syntax as the search and the filters |
| **Compare two logs** | Open a second log and see what moved, cell by cell |
| **Insights** | Findings computed from the samples, each carrying the arithmetic behind it |
| **Calculators** | Fifteen, from injector sizing to drag-strip correlations, each stating what it does not model |
| **Live connection** | Serial, Bluetooth LE and Wi-Fi; 5–200 Hz; recording flushed row by row |
| **Tune editing** | Tables and settings pages built from the firmware's own definition; send and burn separate, as they are on the ECU |
| **Export** | CSV and PNG from every view; CSV that opens again here and pastes into a tuning app |
| **Appearance** | Fourteen colour schemes, each with a trace palette checked for contrast and for protanopia and deuteranopia |
| **AI agent access** | An optional local MCP server, off at every launch, bound to loopback, with every write still gated by a dialog in the window |

## Supported hardware

| | Connection | Definition file needed |
| --- | --- | --- |
| MegaSquirt, MicroSquirt | Serial / USB | Yes — a TunerStudio `.ini` |
| rusEFI | Serial / USB | Yes |
| Speeduino | Serial / USB | Yes |
| MaxxECU | Serial / USB | No |
| Any OBD2 vehicle | ELM327 over USB, Bluetooth LE or Wi-Fi | No |
| Subaru, over SSM | Serial / USB | No — you supply an address list |

## Requirements

- Windows 10 version 1809 (build 17763) or later, x64
- Nothing else. The installer is self-contained, about 54 MB

To build from source you also need the
[.NET 10 SDK](https://dotnet.microsoft.com/download).

## Quick start

Install the MSI, then:

```powershell
# or just double-click, or drag a log onto the window
OpenLogViewer.App.exe "C:\logs\2026-07-26_13.23.25.mlg"
```

From source:

```powershell
dotnet build OpenLogViewer.slnx -c Release
dotnet run --project src/OpenLogViewer.App -c Release
```

Then follow [Getting started](docs/getting-started.md) — open a log, plot some
channels, and build a table from it. About ten minutes.

## The guide is in the application

**Help ▸ How to use this app**, or the **Guide** button in the toolbar.
Seventeen sections, searchable across all of them, with the keyboard shortcuts
against the things they do.

In the application rather than behind a link because of where this gets used: a
laptop plugged into a car, often in a garage with no internet and no signal, is
exactly where somebody needs to look something up. It is the same reasoning that
makes the installer self-contained.

## Documentation

The full set lives in **[docs/](docs/README.md)**.

| | |
| --- | --- |
| [Getting started](docs/getting-started.md) | Install, open a log, confirm it worked |
| [Installation](docs/installation.md) | Requirements, the installer, building from source |
| [User guide](docs/user-guide.md) | Channels, the plot, presets, search, comparison, export |
| [Histogram and scatter](docs/histogram-and-scatter.md) | Binning a log into a table; plotting samples unaveraged |
| [VE calibration](docs/ve-calibration.md) | Suggesting a fuel table from logged mixture |
| [Calculated channels](docs/calculated-channels.md) | Expression syntax and examples |
| [Live connection](docs/live-connection.md) | Connecting to a controller, recording, gauges |
| [OBD2](docs/obd2.md) | ELM327 adapters, batching, fault codes |
| [Subaru SSM](docs/subaru-ssm.md) | Subaru's own protocol, and the parameter file you supply |
| [Editing a tune](docs/tune-editing.md) | Tables, settings, send, burn, saved `.msq` files |
| [Configuration](docs/configuration.md) | Every setting, default, range and file location |
| [Command line](docs/command-line.md) | All command-line options |
| [Troubleshooting](docs/troubleshooting.md) | Symptoms, causes, and what to check |
| [AI agent access (MCP)](docs/mcp-server.md) | Tool inventory and what is not bypassed |
| [Firmware definitions and channels](docs/ini-and-channels.md) | How `.ini` files are found, read and mapped |
| [The MLG log format](docs/mlg-format.md) | The binary `MLVLG` container, field by field |
| [Architecture](docs/architecture.md) | Code layout and data flow |
| [Development](docs/development.md) | Building, testing, contributing |

## Where your files go

```text
C:\Users\<you>\OpenLogViewer\
    Logs\             live recordings
    Exports\          where Export starts
    ECU definitions\  firmware .ini files you supply
```

Settings are separate, under `%APPDATA%\OpenLogViewer\`. Nothing is ever written
next to the program, so it is content installed read-only under Program Files.

Deliberately **not** "My Documents": that is redirected into OneDrive on most
machines, which uploads every recording while it is still being written.

Details: [Configuration ▸ Where files go](docs/configuration.md#where-files-go).

## Project layout

```text
src/OpenLogViewer.Core     format readers, protocols, tune model, analysis (no UI)
src/OpenLogViewer.App      the WPF application, including the MCP server
tools/OpenLogViewer.Dump   console decoder / reader regression harness
tools/OpenLogViewer.Probe  read-only vehicle probe, for protocol research
tests/                     2,347 tests across two suites
installer/                 WiX 5 MSI
docs/                      documentation
```

`OpenLogViewer.Core` targets plain `net10.0` and has no WPF reference, so it can
be reused from a console tool, a test, or a non-Windows host.

See [Architecture](docs/architecture.md).

## Icon and logo

`AppIcon.ico` carries ten sizes (16 → 256) with **two different pieces of art**:

- **64 px and above** — the full illustration ([`docs/logo.png`](docs/logo.png)),
  which is also the project logo above
- **Below 64 px** — a drawn mark ([`docs/mark.png`](docs/mark.png)): an RPM trace
  climbing through gear shifts, with a boost trace beneath it

The photographic artwork is unreadable at taskbar size, so the small entries get
a purpose-drawn glyph instead. The mark is rendered natively at each size rather
than downscaled, and sits on a mid-dark slate tile that holds its own against
both light and dark taskbars.

## On provenance

Original work. This project is not derived from any existing application, and no
proprietary software was decompiled, disassembled, or otherwise reverse
engineered to build it.

The MLG format was reconstructed by reading sample `.mlg` files directly. The
container is self-describing — it carries its own channel names, data types,
units and scaling factors — so the layout was recovered by inspecting file
headers, testing candidate record strides against physical plausibility (battery
voltage sits at 12–14.5 V; coolant temperature reaches 160–200 °F), and
confirming that the resulting record count divides the file exactly. That process
is documented in [`docs/mlg-format.md`](docs/mlg-format.md) so it can be
independently checked.

## License

MIT — see [LICENSE](LICENSE).

Third-party components, and what the installer redistributes, are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Everything bundled is MIT. No
copyleft code or data is included, and no ECU definition files are
redistributed — the application reads the ones already on your machine.

> **WARNING:** This is a tool for engine tuning. It can change the tune in a
> connected ECU and burn that change to flash — each behind its own confirmation
> saying what is about to happen — and a write takes effect immediately on a
> running engine. An incorrect fuel or ignition value can cause detonation or
> engine damage within seconds. Tuning decisions made with this software are
> yours; the authors accept no liability for engine damage.
