# Command line

OpenLogViewer accepts command-line options, mainly so a scripted run can drive it
without a person at the keyboard. Every screenshot in this documentation set is
produced this way.

- [Usage](#usage)
- [Where errors go](#where-errors-go)
- [Opening a file](#opening-a-file)
- [Appearance and view](#appearance-and-view)
- [Connecting](#connecting)
- [Histogram and scatter](#histogram-and-scatter)
- [Tune files](#tune-files)
- [Output and exit](#output-and-exit)
- [Capturing parts of the interface](#capturing-parts-of-the-interface)
- [The dump tool](#the-dump-tool)
- [Known issues](#known-issues)

---

## Usage

```powershell
OpenLogViewer.App.exe [<log file>] [options]
```

Or from source:

```powershell
dotnet run --project src/OpenLogViewer.App -c Release -- [<log file>] [options]
```

The log file is the one argument that is not a switch or a switch's value, so it
can appear anywhere in the line.

## Where errors go

**This is a Windows GUI application. It has no console attached, so anything
written to one goes nowhere.**

A scripted run reports failures to a file instead:

```text
%TEMP%\openlogviewer-run.log
```

Check it when `--connect`, `--screenshot` or `--export` appears to do nothing.
Unhandled exceptions on both the interface thread and background threads are
recorded there too.

## Opening a file

| Option | Value | Description |
| --- | --- | --- |
| *(bare path)* | A log file | Opens it at startup, before the window is shown |
| `--open-tune` | A `.msq` path | Opens a saved tune, with no controller attached |
| `--page` | A page name | With `--open-tune` or `--settings`, opens that settings page |
| `--settings` | An `.ini` path | Opens a definition's settings pages with no controller behind them |
| `--live-page` | A page name | Opens a settings page of the tune already loaded |

```powershell
OpenLogViewer.App.exe "C:\logs\2026-07-26_13.23.25.mlg"
OpenLogViewer.App.exe --open-tune "C:\tunes\daily.msq" --page Rev
```

## Appearance and view

| Option | Value | Description |
| --- | --- | --- |
| `--theme` | A theme id | Starts in a colour scheme for one run, without changing the saved preference. Ids are the lower-case hyphenated scheme names, e.g. `solarized-dark` |
| `--stacked` | — | Stacked traces instead of overlaid |
| `--gauges` | — | Opens the Gauges view |
| `--calibration` | *(optional)* table name | Opens the Calibration view, optionally at a named table |
| `--guide` | *(optional)* section name | Opens the in-app guide, optionally at a section |
| `--insights` | — | Opens the findings window for the loaded log |
| `--select` | `from,to` | Marks a span, in the log's own time units |
| `--find` | An expression | Opens the search bar and frames the first hit |

```powershell
OpenLogViewer.App.exe log.mlg --theme nord --stacked
OpenLogViewer.App.exe log.mlg --find "RPM > 4000 && TPS > 80"
```

## Connecting

| Option | Value | Description |
| --- | --- | --- |
| `--connect` | A port, e.g. `COM8` | Connects to a controller on that serial port |
| `--obd2` | — | With `--connect`, treats the port as an OBD2 adapter rather than a tuning cable |
| `--connect-ble` | A device name or id | Connects to a Bluetooth LE OBD2 adapter |
| `--connect-wifi` | `host:port`, or `auto` | Connects to a Wi-Fi OBD2 adapter. `auto` tries the known addresses |
| `--connect-ssm` | A port | Connects to a Subaru over SSM |
| `--connect-menu` | A menu entry | Connects via the entry with that label in the connect menu |
| `--settle` | Milliseconds | Waits for the session to settle before doing anything else |
| `--mcp` | — | Arms the local MCP server at startup |

```powershell
OpenLogViewer.App.exe --connect COM8 --settle 15000 --export "C:\out"
OpenLogViewer.App.exe --connect-wifi 192.168.0.10:35000
OpenLogViewer.App.exe --connect-wifi auto
```

> **NOTICE:** `--mcp` is not a setting and not persistence. It is typed afresh
> every launch, which is the same act as ticking the menu item. See [AI agent
> access (MCP)](AI-agent-access-MCP).

## Histogram and scatter

| Option | Value | Description |
| --- | --- | --- |
| `--histogram` | — | Opens the histogram view |
| `--scatter` | — | Opens the scatter view |
| `--tune-axes` | An index | Uses the tune's own table axes, by index |
| `--compare` | A channel name | Sets **Compare against** |
| `--z` | A channel name | Sets the value channel |
| `--count-colour` | — | Colours by sample count |
| `--count-value` | — | Uses sample count as the cell value (histogram only) |
| `--cell` | `column,row` | Activates a table cell, tracing it back to the log |
| `--mark` | `column,row` | The same for a scatter block |
| `--ve` | — | Turns on **Suggest a new fuel table** |
| `--ve-values` | — | With `--ve`, shows the new numbers rather than the percentage move |

```powershell
OpenLogViewer.App.exe log.mlg --histogram --tune-axes 0 --z AFR --compare "AFR 1 Target" --ve
```

## Tune files

| Option | Value | Description |
| --- | --- | --- |
| `--save-tune` | A `.msq` path | Writes the tune in hand to a file |
| `--compare-tune` | A `.msq` path | Reports what a file and the tune in hand disagree about |
| `--plan-restore` | A `.msq` path | Reports what restoring that tune would change, and does none of it |
| `--tune-cell` | `column,row[,nudge]` | Selects a tune-table cell and nudges it. Local to the copy on screen; nothing is sent or burned |

> **NOTICE:** **There is deliberately no flag that carries out a restore.** It is
> the largest change this application can make to an engine, and it is not
> something to fall out of a command line. See [Editing a
> tune](Editing-a-tune#restoring-a-saved-tune).

## Output and exit

These two run their work once layout has settled, then exit.

| Option | Value | Description |
| --- | --- | --- |
| `--screenshot` | A `.png` path | Renders the window to a PNG and exits |
| `--pointer` | `x,y` | Places the cursor first, as fractions of the plot area, so the hover readout appears in the capture |
| `--export` | A folder | Writes every export for the current view without dialogs, then exits |

```powershell
OpenLogViewer.App.exe log.mlg --screenshot out.png
OpenLogViewer.App.exe log.mlg --pointer 0.42,0.55 --screenshot out.png
```

The application renders itself rather than being captured from another process,
because capturing from outside is unreliable under Desktop Window Manager
composition.

## Capturing parts of the interface

Each of these renders one piece of the interface to a PNG and exits. They exist
for documentation and regression captures.

| Option | Value | Description |
| --- | --- | --- |
| `--menu` | A `.png` path | The connect menu |
| `--scan-menu` | A `.png` path | The connect menu after a device scan |
| `--top-menu` | Header, then a `.png` path | One of the menu bar's drop-downs, e.g. `--top-menu View out.png` |
| `--calculators` | Tab name, then a `.png` path | One calculator, e.g. `--calculators Injectors out.png` |
| `--power` | A `.png` path | The power estimate over the loaded log |
| `--faults` | A `.png` path | The fault codes for the current OBD2 connection. Needs a `--connect` ahead of it |

## The dump tool

`OpenLogViewer.Dump` decodes a log and prints a summary to the console. It doubles
as the regression check for the readers.

```powershell
dotnet run --project tools/OpenLogViewer.Dump -c Release -- <log> [options]
```

| Option | Description |
| --- | --- |
| `--channels` | Lists every channel with its units and range |
| `--categories` | Shows how each channel was grouped — the quickest way to check the classifier against a new firmware |
| `--tune` | Lists the tune axes found in the log |

```text
=== 2026-07-26_13.23.25.mlg ===
  format    : MLG v2
  channels  : 179
  samples   : 37,328
  duration  : 2790.98 s  (Time base)
  markers   : 22
    RPM       RPM             0 .. 6960
    MAP       kPa           9.2 .. 176.5
    Batt V    v             8.7 .. 15.2
```

```text
Common (17)
    AFR, AFR 1 Target, AFR load, Batt V
    Boost psi, CLT, Duty Cycle1, Dwell ...
Ignition (24)
    Ign load, Knock in, SPK: Base Spark Advance, SPK: Knock retard ...
```

## Known issues

> **NOTICE:** **`--insights` and `--screenshot` together hang.** The findings
> window is modal, so the capture queued behind it never runs and the application
> never exits. Use them separately.

## Related

- [Configuration](Configuration) — the persistent equivalents of some of these
- [Development](Development)
- [AI agent access (MCP)](AI-agent-access-MCP)
