# Getting started

The shortest reliable path from a fresh installation to something useful on
screen. Allow about ten minutes.

- [1. What OpenLogViewer is](#1-what-openlogviewer-is)
- [2. Requirements](#2-requirements)
- [3. Install](#3-install)
- [4. Open a log](#4-open-a-log)
- [5. Plot some channels](#5-plot-some-channels)
- [6. Read a value](#6-read-a-value)
- [7. Build a table from the log](#7-build-a-table-from-the-log)
- [8. Verify everything is working](#8-verify-everything-is-working)
- [9. Next steps](#9-next-steps)

---

## 1. What OpenLogViewer is

A datalog viewer and live tuning tool for engine control units.

An **ECU** (Engine Control Unit) is the computer that runs an engine. A
**datalog** — or just *log* — is a recording of what its sensors read, sample by
sample, taken either by the ECU itself or by a tuning application such as
TunerStudio. A **tune** is the set of tables and settings the ECU runs from.

OpenLogViewer does three things:

1. **Opens recorded logs** and lets you read them — plotted against time, binned
   into a table, or plotted as a scatter.
2. **Connects live** to a controller or to any OBD2 vehicle, showing readings as
   they arrive and recording them to a file.
3. **Reads and edits the tune** on a connected controller, and saves it to a
   file.

This page covers the first of those, because it needs no hardware.

## 2. Requirements

| | |
| --- | --- |
| Operating system | Windows 10 version 1809 (build 17763) or later |
| Architecture | x64. An ARM64 build can be produced from source |
| Runtime | None. The installer carries its own copy of .NET |
| Download | The installer is about 54 MB |
| Hardware | None, to read a recorded log |

Building from source additionally needs the .NET 10 SDK. See
[Installation](installation.md).

## 3. Install

Run `OpenLogViewer-<version>-win-x64.msi`.

> **NOTICE:** The installer is not code-signed. Windows SmartScreen shows a
> warning the first time you run it. Choose **More info ▸ Run anyway**. This is
> the expected behaviour for an unsigned installer and does not indicate a
> problem with the file.

**Expected result:** OpenLogViewer appears in the Start menu. It does *not*
take over the double-click action for `.mlg` or `.msl` files — it registers
itself under **Open with** only, so an existing TunerStudio installation keeps
those associations.

## 4. Open a log

You need a log file. Any of these will do:

- A `.mlg` or `.msl` from MegaSquirt or TunerStudio
- A `.csv` exported from almost any tuning application
- A MaxxECU `.MaxxECU-Zip-log`

Then either:

- **File ▸ Open log…** (`Ctrl+O`), or
- Drag the file onto the window.

Nothing is assumed from the file extension — the content is examined instead, so
a log renamed by a browser or an email client still opens.

**Expected result:** the window title becomes the file name, the channel list
fills the left-hand sidebar, and the status bar reports the channel count,
sample count and duration.

**If it does not open:** the message names what was wrong with the file rather
than saying that something failed. See
[Troubleshooting ▸ A log will not open](troubleshooting.md#a-log-will-not-open).

## 5. Plot some channels

Tick channels in the sidebar list to plot them. To get going quickly, click
**Common** above the list — it plots the set most logs are read for.

Two more controls are worth knowing immediately:

- **Hide unused** is on by default. Logs routinely declare channels that never
  move — 98 of 179 in one sample log — and hiding them is what makes the rest
  findable. Those channels are still recorded and still exported.
- The search box at the top of the list matches on channel name, units or
  category.

**Expected result:** traces appear on the plot, each in its own colour, with the
channel's row in the sidebar showing its range.

## 6. Read a value

Move the pointer across the plot.

**Expected result:** every channel row shows that channel's value at the
pointer. The trace nearest the pointer thickens and its row highlights, and a
card gives its value there plus its highest and lowest reading and the moment
each occurred.

Two more gestures:

| Gesture | What it does |
| --- | --- |
| Scroll wheel | Zoom in and out at the pointer |
| Drag | Pan |
| Double-click | Fit the whole log |
| `Shift`+drag | Mark a span; every row switches to min … max and the average over it |

## 7. Build a table from the log

This is what separates a log viewer from a tuning tool. Switch to **Histogram**
in the toolbar.

Pick three channels: two for the axes and one for the value. A sensible first
attempt on a fuel log is RPM across, MAP or load down, and AFR as the value.

**Expected result:** a table shaped like the one in your ECU, each cell shaded
by its value, with the sample count behind each cell available on hover.

If the log is a `.mlg`, it carries the tune it was recorded with. Choose one of
the tune's own tables under **Axis breakpoints** and the table is binned onto
exactly those breakpoints — cell for cell against the table in TunerStudio.

See [Histogram and scatter](histogram-and-scatter.md) for what this can and
cannot tell you.

## 8. Verify everything is working

Three checks, in order of how much they prove:

1. **The log opened.** The status bar reports a channel count and a duration
   that match what you expect from the recording.
2. **A value reads correctly.** Hover a known channel — battery voltage should
   sit between about 12 V and 14.5 V on a running engine, coolant temperature
   should climb through warmup. A channel reading a plausible-looking constant
   when it should be moving is the failure worth catching here.
3. **The table is populated.** Build a histogram and turn on **Colour by sample
   count**. The cells the drive actually visited should be the ones with data in
   them; an evenly-populated table across the whole map usually means the axes
   are wrong.

## 9. Next steps

- [User guide](user-guide.md) — everyday operation in full
- [Live connection](live-connection.md) — connect to a controller
- [OBD2](obd2.md) — connect to any standard vehicle with a cheap adapter
- [VE calibration](ve-calibration.md) — turn a log into a suggested fuel table
- **Help ▸ How to use this app** — the same guide, offline, inside the
  application
