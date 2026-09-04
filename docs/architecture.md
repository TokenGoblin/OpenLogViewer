# Architecture

How the code is laid out, and how data moves through it. For contributors; a user
does not need any of this.

- [The shape of it](#the-shape-of-it)
- [Projects](#projects)
- [OpenLogViewer.Core](#openlogviewercore)
- [OpenLogViewer.App](#openlogviewerapp)
- [Data flow: opening a log](#data-flow-opening-a-log)
- [Data flow: a live session](#data-flow-a-live-session)
- [Data flow: writing to an ECU](#data-flow-writing-to-an-ecu)
- [Threading](#threading)
- [Dependencies](#dependencies)
- [Design rules that are load-bearing](#design-rules-that-are-load-bearing)

---

## The shape of it

```text
┌──────────────────────────────────────────────────────────────┐
│  OpenLogViewer.App          WPF. Windows-only.               │
│                                                              │
│   MainWindow ── MainViewModel ── views (LogPlot, Histogram,  │
│        │              │           Scatter, Gauges, Tune)     │
│        │              │                                      │
│        │              └── Mcp/  local MCP server (loopback)  │
│        │                                                     │
│        └── ThemeCatalog, SerialPortNames, BleDevices          │
└───────────────────────────┬──────────────────────────────────┘
                            │  no WPF types cross this line
┌───────────────────────────┴──────────────────────────────────┐
│  OpenLogViewer.Core         plain net10.0. No UI.            │
│                                                              │
│   readers ──▶ LogDocument ──▶ analysis (histogram, scatter,  │
│                    ▲            VE, search, insights)        │
│                    │                                         │
│   live sources ────┘                                         │
│                                                              │
│   protocols (MS, MaxxECU, ELM327, SSM) · tune model · stores │
└──────────────────────────────────────────────────────────────┘
```

The line in the middle is the important one. **`OpenLogViewer.Core` targets plain
`net10.0` and has no WPF reference**, so it can be built and tested from a console
tool, a test host, or a non-Windows machine. Everything a person points at an
engine — the readers, the protocols, the tune model, the arithmetic — lives below
it.

## Projects

```text
src/OpenLogViewer.Core     format readers, protocols, tune model, analysis
src/OpenLogViewer.App      the WPF application
src/OpenLogViewer.App/Mcp  the local MCP server
tools/OpenLogViewer.Dump   console decoder and reader regression harness
tools/OpenLogViewer.Probe  read-only vehicle probe, for protocol research
tests/OpenLogViewer.Tests      core: readers, analysis, protocols
tests/OpenLogViewer.App.Tests  the view model, driven end to end
installer/                 WiX 5 MSI
docs/                      this documentation set
```

## OpenLogViewer.Core

Roughly by subject:

| Area | Files | What they do |
| --- | --- | --- |
| **Log model** | `LogDocument`, `LogChannel`, `ChannelStatistics` | What an opened log is: named channels of `double`, sharing a time base. A missing reading is `NaN` |
| **Readers** | `ILogReader`, `LogReaderFactory`, `MlgReader`, `DelimitedLogReader`, `MaxxLogReader` | Turn a file into a `LogDocument`. `LogReaderFactory` asks each reader whether it can read the file — nothing is decided from the extension |
| **Channel meaning** | `ChannelClassifier`, `ChannelRoles`, `ChannelUnits`, `UnitConvert` | Group channels by system, and find the channel that does a job (the wideband, the target, the load axis) |
| **Analysis** | `HistogramTable`, `ScatterPlot`, `VeAnalysis`, `WidebandDelay`, `LogSearch`, `LogInsights`, `LogComparison` | Everything that reads a `LogDocument` and produces a conclusion |
| **Derived data** | `MathExpression`, `MathChannel`, `DerivedChannels`, `PowerEstimate` | Channels computed from other channels |
| **Firmware definitions** | `MsqIni`, `IniCatalog`, `IniDefines`, `TuneLayout`, `TuneDialogs`, `DialogCondition` | Read a TunerStudio `.ini`: output channels, datalog names, settings pages, and the conditions that show or hide a field |
| **Tune model** | `EcuTune`, `TuneEdit`, `TuneSettingsEdit`, `TuneCurveEdit`, `TuneCompare`, `TuneRestore`, `MsqFile`, `MsqWriter`, `MsqApply` | The tune in memory, the edits made to it, and the `.msq` on disk |
| **Protocols** | `MsProtocol`, `RealtimeDecoder`, `MaxxProtocol`, `Elm327`, `Obd2Pids`, `Obd2Faults`, `Ssm` | Wire formats |
| **Transports** | `IEcuTransport`, `SerialEcuTransport`, `WifiEcuTransport` | Bytes in and out. Bluetooth LE lives in the App because it needs WinRT |
| **Live sources** | `ILiveSource`, `LiveSession`, `TunerStudioSource`, `MaxxEcuSource`, `Elm327Source`, `SsmSource` | Poll a controller and append samples to a growing `LogDocument` |
| **Stores** | `JsonSettingsFile`, `SettingsStore`, `PresetStore`, `FilterStore`, `MathChannelStore`, `ChannelStyle`, `Workspace` | The JSON files under `%APPDATA%`, and where user files go |
| **Calculators** | `TuningMath`, `TurboSizing`, `Gearing`, `DragStrip`, `OctaneBlend`, `Intercooling`, `EngineGeometry`, … | Standalone arithmetic, each with its own tests |
| **The manual** | `Guide` | The in-app guide, written as data so it can be searched and tested |

### One design decision worth calling out

**The in-app guide is data, not markup.** `Guide.cs` holds sections and entries as
records. That is what lets it be searched across all sections, re-themed with the
rest of the application, and checked by a test that asserts no section is empty
and no text was left blank — which is the failure a hand-written help page
actually has.

## OpenLogViewer.App

| Area | Files | Notes |
| --- | --- | --- |
| **Shell** | `MainWindow.xaml`, `MainWindow.xaml.cs`, `App.xaml.cs` | The menu, the toolbar, and command-line handling |
| **State** | `MainViewModel` (partial, several files), `Mvvm`, `WorkspaceMode` | One view model for the whole window |
| **Views** | `LogPlot`, `HistogramView`, `ScatterView`, `GaugeView`, `TuneTableView`, `CurveView` | Custom-drawn. None is a control tree |
| **Windows** | `CalculatorsWindow`, `InsightsWindow`, `OverviewWindow`, `PowerWindow`, `FaultsWindow` | |
| **Theming** | `Theme`, `ThemeCatalog`, `ThemeManager`, `ColorMath` | Palettes and the derived heat ramps |
| **Devices** | `SerialPortNames`, `BleDevices`, `BleEcuTransport` | Windows-specific device enumeration and Bluetooth LE |
| **Writes** | `WriteConfirmation`, `SavedTuneCommands` | Every ECU write passes through the confirmation |
| **MCP** | `Mcp/` | The local server and its tool groups |

`MainViewModel` is a `partial class` split across several files by subject. It is
large because the window is one workspace rather than a set of independent
screens: a live session, a histogram built from it, and a tune read off the same
controller are all the same state.

### The MCP server

`Mcp/McpServerHost` hosts an HTTP transport on `127.0.0.1:7071`, off by default.
Tools are grouped by subject — `LogTools`, `AnalysisTools`, `LiveTools`,
`TuneTools`, `EcuWriteTools`, `FaultTools`, `TuneFileTools`, `OverviewTools`,
`AppTools` — and every one of them marshals onto the interface thread through
`UiDispatcher`, because they act on **this window**, not a second invisible copy.

Full documentation: [AI agent access (MCP)](mcp-server.md).

## Data flow: opening a log

```text
path ──▶ LogReaderFactory.Load
             │  asks each ILogReader "can you read this?"
             │  (content, never the extension)
             ▼
         MlgReader / DelimitedLogReader / MaxxLogReader
             ▼
         LogDocument  ── channels, units, time base
             ▼
         ChannelClassifier ── groups; ChannelRoles ── finds the wideband, target, load
             ▼
         MainViewModel ── + math channels, filters, presets, pinned styles
             ▼
         LogPlot / HistogramView / ScatterView
```

## Data flow: a live session

```text
IEcuTransport (serial | Wi-Fi | BLE)
      ▼
protocol (MsProtocol | MaxxProtocol | Elm327 | Ssm)
      ▼
ILiveSource ── polls at the configured rate
      ▼
LiveSession ── appends samples to a growing LogDocument
      │              and writes each row to disk as it arrives
      ▼
MainViewModel ── the same state a file produces
```

Because a live session produces an ordinary `LogDocument`, every analysis path
works on it unchanged. That is not a coincidence; it is the reason the seam is
where it is.

**Recording writes through, not at the end.** A session ends by a pulled cable at
least as often as by being stopped.

## Data flow: writing to an ECU

```text
edit in TuneTableView / settings page
      ▼
TuneEdit / TuneSettingsEdit ── clamped to the firmware's declared range
      ▼
WriteConfirmation ── a dialog in this window, always
      ▼
MsProtocol.Write ── the bytes that differ, gathered into runs
      ▼
read back and compare ── before it is called done
      ▼
(separately) Burn ── only the pages that were written
```

**Every path to a write goes through `WriteConfirmation`, including the MCP
tools.** An agent that calls a write tool waits on the same dialog; the call does
not return until somebody answers it.

## Threading

| Thread | What runs on it |
| --- | --- |
| Interface thread | All view models, all views, all confirmations |
| Poll thread | A live source's request/reply loop |
| Kestrel threads | MCP requests, which marshal to the interface thread before touching anything |

`SettingsStore` takes a lock around its file and the fields it is written from,
because one setting is written off the interface thread: the poll thread notices
when a batched OBD2 request has killed a link, and the note it writes lands in the
middle of whatever the window happens to be saving.

Settings files are written through a per-write temporary file and moved into
place, so two writers cannot splice one file out of two versions.

## Dependencies

Four, all Microsoft-published:

| Package | Project | Why |
| --- | --- | --- |
| `System.IO.Ports` | Core | Serial access for a live connection. Part of .NET, but a package rather than in the framework since .NET Core |
| `System.Management` | App | Naming COM ports in the connect menu — the difference between picking a Bluetooth module and guessing |
| `ModelContextProtocol.AspNetCore` | App | The MCP server |
| `Microsoft.AspNetCore.App` (framework reference) | App | Kestrel, to host the MCP transport from a non-Web SDK project |

Everything bundled is MIT. No copyleft code or data is included, and **no ECU
definition files are redistributed** — the application reads the ones already on
your machine. See [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

## Design rules that are load-bearing

These are not style preferences. Breaking one changes what the software does to an
engine.

1. **Core has no WPF reference.** It is enforced by the project file and by the
   tests that build against it.
2. **The MCP server binds `127.0.0.1` and nothing else,** armed or not. There is a
   test that reads the source to keep it that way.
3. **The MCP server is off at every launch and never persisted.** A listener that
   survives a restart is one you can forget about.
4. **Every write and burn passes through a confirmation in the window.** No code
   path may write without one.
5. **A wrong firmware definition must refuse, not decode.** Decoding with the
   wrong `.ini` produces reasonable-looking nonsense, which is worse than a
   refusal.
6. **Missing readings are `NaN` and propagate.** They must not be silently
   treated as zero, or as `false` in a comparison.
7. **Values are clamped to the firmware's declared range,** not to the storage
   range.
8. **Nothing is ever written next to the executable.**

## Related

- [Development](development.md) — building, testing, contributing
- [Firmware definitions and channels](ini-and-channels.md)
- [The MLG log format](mlg-format.md)
- [AI agent access (MCP)](mcp-server.md)
