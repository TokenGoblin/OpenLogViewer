<p align="center">
  <img src="docs/logo.png" alt="OpenLogViewer" width="440">
</p>

<h1 align="center">OpenLogViewer</h1>

<p align="center">
  A native Windows datalog viewer for MegaSquirt, TunerStudio and other ECU logs.<br>
  Built on .NET 10 and WPF, with no third-party dependencies.
</p>

<p align="center">
  <img src="docs/mark.png" alt="" width="64">
</p>

---

Open a `.mlg`, `.msl` or `.csv` log, pick channels, and scrub through them.

![OpenLogViewer](docs/screenshot.png)

## Supported logs

| Source | Format | Status |
|---|---|---|
| MegaSquirt / TunerStudio | `.mlg` binary | **Verified** against MS2 and MS3 logs, incl. markers and flag bytes |
| MegaSquirt / TunerStudio | `.msl` text | **Verified** against 43 real logs, MS3 1.3.3 through 3.3 |
| rusEFI | `.mlg`, `.msl` | Same `MLVLG` container and text layout |
| MaxxECU, Haltech, MoTeC, Link, AEM, ECUMaster, Holley, HP Tuners, Speeduino | CSV / TSV export | Handled by the generic text reader |
| Anything else | delimited text | Handled if it has a header row and numeric columns |

Nothing is assumed from the file extension — content is sniffed instead. The
text reader auto-detects encoding, delimiter, header and units rows, decimal
separator and time base, which is what lets one code path cover exports from
tools that agree on almost nothing:

- **Delimiters** tab, comma, semicolon or pipe, chosen by which one splits the
  file most consistently rather than by which is most frequent
- **Decimal comma** (`1234,5`) as used by European-locale exports
- **Quoted fields** containing the delimiter (RFC 4180)
- **Units** from a dedicated units row *or* bracketed in the header
  (`MAP (kPa)`, `RPM [rpm]`, `CLT {degC}`)
- **Encoding** UTF-8 or Latin-1 — older TunerStudio exports are ISO-8859-1,
  where `°F` is a single byte that is not valid UTF-8
- **Time base** in seconds, milliseconds or minutes, from a wall-clock timestamp
  column, or synthesised from the sample index when there is no usable time
  column. A log that starts at t=2178 s, or at a negative time, is fine
- **Duplicate channel names** disambiguated by units — MS3 emits
  `Fuel Consumption` twice, in GPH and l/hr

If you have a log from an ECU not listed above that does not load, the format is
usually easy to add — the reader is one file, and `--categories` on the dump tool
shows how its channels were understood.

## Histogram / table mode

Switch to **Histogram** in the toolbar to bin the log into a table — the same
shape as the VE or AFR table in the ECU, so a drive can be read against the tune
it came from.

Pick any three channels: two axes and a value. Each cell reduces the samples that
landed in it by mean, min, max or count, over a table up to 40 × 40. *Only the
zoomed time range* restricts it to the window you zoomed to in the log view, so a
table can be built from a single pull rather than the whole drive. Hovering a
cell reports the bin, the value, and how many samples back it.

Cells use a **single-hue sequential ramp**, light for high and dark for low, not
the green/yellow/red seen elsewhere. A rainbow ramp has no inherent order — the
eye cannot rank yellow against cyan — and it collapses under colour-vision
deficiency and in greyscale. Lightness ranks unambiguously for everyone. The ramp
is validated against this app's surface: the low end holds 2.71:1 against it, so a
barely-populated cell still reads as distinct from an empty one, and cell text
flips between light and dark ink to stay legible on every step.

*Colour by sample count* re-shades the same table by how much data backs each
cell, which is the quick way to see which parts of the table the drive actually
exercised.

**Click a cell to trace it back to the log.** The view switches to the plot,
frames the samples that produced that cell and marks them.

An engine passes through the same RPM and load many times in a drive, so a cell
is almost never one stretch of the recording — showing the span from its first
to its last sample would cover most of the log. The samples are grouped into
*visits* instead: the longest is framed and selected, every other visit is
shaded, and the status line reports how many there were. A cell averaged over
twelve separate passes is a very different thing from one sustained pull, and
the table alone cannot show that.

### Axis breakpoints from the tune

Uniform bins spanning the observed range never line up with the table you are
actually editing, because ECU axes are not uniform — they are tight at idle and
wide up top.

Every `.mlg` embeds the `.msq` it was recorded with, so the tune's own axes can
be read straight out of the log. Pick one under **Axis breakpoints** and the
table is binned onto exactly those breakpoints, cell for cell against the table
in TunerStudio. VE, spark and AFR-target tables are offered when present:

```
VE table 1  (16×16)
  frpm_table1 [RPM]: 500 800 1100 1400 1800 2200 2600 3000 3500 4000 4300 4700 5200 5700 6100 6500
  fmap_table1 [kPa]: 30 40 50 60 70 80 90 100 120 140 160 180 200 230 260 300
```

Samples are assigned to the **nearest** breakpoint, which is how a value between
two rows is attributed in a tuning table. Values beyond either end fall to the
nearest one rather than being discarded.

Two quirks of the format are handled, both of which would corrupt a table
silently. Firmwares pad an axis out to the table's width by repeating the top
value, which would create zero-width bins, so consecutive duplicates are
collapsed. And the `…doz` axis variants are stored *rolled* rather than in order
(`5200 5700 6100 6500 502 801 …`), so any axis that is not ascending is rejected
outright rather than scrambling the rows.

`--tune` on the dump tool lists the axes found in a log.

### Data filters

A table built from a whole drive averages warmup, overrun and idle into the same
cells as the pulls you care about, and describes none of them. Filters throw out
the samples that do not belong.

Each filter is a condition on a channel — `CLT ≥ 160`, `TPS > 1`, `AFR between 9
and 20` — and they combine with AND: a sample must satisfy every ticked
condition to be counted. Opening a log offers suggestions for the channels it
has, always switched off, so loading a log never silently changes what the table
counts. Add your own with **+ Add filter**; right-click one to delete it.

Two things follow from filtering that are easy to miss:

- The status line reports how many samples were excluded, so a suspiciously
  sparse table explains itself rather than looking broken.
- **The axes re-scale to the samples that survive.** Filtering to warm running
  also tightens the RPM and MAP range onto that data, which is usually what you
  want — but it means two differently-filtered tables are not directly
  comparable cell for cell.

Filters are matched to channels by name and persist between sessions in
`%APPDATA%\OpenLogViewer\filters.json`. A filter naming a channel the log does
not have is reported and skipped, never applied as "reject everything".

## Features

- **Binary `.mlg` support**, including packed flag bytes and interleaved marker
  records — see [`docs/mlg-format.md`](docs/mlg-format.md)
- Channel sidebar with search, per-channel range, and a live value readout that
  follows the cursor
- **Channels grouped by system** — Common, Engine, Air & boost, Fuel, Ignition,
  Temperature, Idle, Electrical, Diagnostics — in collapsible sections
- **Hide unused**, on by default: logs routinely declare channels that never
  move (98 of 179 in one sample), and hiding them makes the rest findable
- Sort by category, A–Z, or plotted-first; **Common** plots the usual set in one
  click
- **Named presets** — plot the channels you want, click *+ Save*, name it, and it
  becomes a chip you can click to restore that exact selection. Presets persist
  between sessions and are matched by channel name, so one saved on a log applies
  to any other log that shares those names. Right-click a preset to overwrite or
  delete it. Stored as readable JSON at
  `%APPDATA%\OpenLogViewer\presets.json`, which you can hand-edit or copy between
  machines
- **Hover a trace to interrogate it** — the nearest trace to the pointer is
  thickened, its row highlights in the sidebar, and a readout gives its value at
  the cursor plus its maximum and minimum *and the moment each occurs*
- **Jump to a channel's extremes**, three ways — the ▲ / ▼ buttons on any channel
  row, right-click → *Jump to maximum / minimum*, or click the max or min line in
  the hover readout itself. All keep the current zoom and plot the channel first
  if it was not already showing
- **Overlaid or stacked** — overlaid traces, each scaled to its own range, read
  well for phase relationships between channels; stacked lanes give each channel
  its own strip, which is what you want past about four channels. Toggle in the
  toolbar
- **Shift-drag to mark a span** — every channel row switches to min … max and the
  average over that span, and the hover readout reports the span rather than the
  whole log
- Scroll to zoom at the pointer, drag to pan, double-click to fit
- Log markers drawn in place on the timeline
- **Gaps in logging are drawn as gaps.** Paused-and-resumed logs are common, and
  a straight line across an eight-minute pause reads as steady data that was
  never recorded. Traces lift the pen when the step between samples exceeds ten
  times the log's median sample interval
- Min/max envelope decimation, so a 37,000-sample log scrubs smoothly
- **Fourteen colour schemes** — see below
- **Calculated channels** — see below
- **VE Calibration** — see below
- **Export** — see below
- No third-party dependencies

## Calculated channels

*ƒ Add calculated channel* in the sidebar. Define a channel from the ones the
log already has:

```
AFR - AFR Target 1
RPM * Torque / 5252
if(Boost psi > 0, Boost psi, 0)
```

Once built they are ordinary channels: plottable, usable as a histogram axis,
available to filters, and included in an export. They are marked **ƒ** in the
list.

Channel names need no quoting even with spaces in them — names are matched
against the log's own, longest first, so `AFR Target 1` wins over `AFR`. A match
has to end on a word boundary, so `MAPX` is not read as `MAP` followed by an
unexplained `X`.

Operators `+ - * / % ^`, comparisons `< <= > >= == !=`, `&& || !`, and the
functions `abs sqrt min max clamp floor ceil round log log10 exp pow sign if`.
`pi` and `e` are available as constants.

Missing readings propagate — including through comparisons, where returning
"false" for a reading that was never taken would let `if` choose a branch on the
strength of nothing. A result that is not finite becomes a gap rather than an
infinity, which would otherwise take the channel's range with it.

Definitions live in `%APPDATA%\OpenLogViewer\math.json`, are held by name and
expression, and so apply to any log carrying those channels. One that does not
fit the open log is reported in the sidebar rather than dropped.

## VE Calibration

Suggests a new fuel table from logged AFR against the AFR the tune was asking
for. In histogram view, pick one of the tune's own tables under *Axis
breakpoints*, set *Compare against* to the AFR target channel, and tick
**Suggest a new fuel table**.

The reasoning is one line: the engine took in a known amount of air, the ECU
metered fuel for it using the VE number in the cell, and the wideband says what
the mixture actually came out as. Richer than target means the ECU thought there
was more air than there was, so the VE number is too high — scale it by measured
over target.

What makes it usable is what it refuses to do:

- A cell with fewer than **Min samples** is left alone and counted as thin. Two
  crossings on the way somewhere else say more about the transient than about
  the fuelling there.
- A correction larger than **Max change %** is clamped, not applied whole. A
  cell read during an accel-enrichment event can imply a change far bigger than
  the table is actually wrong by.
- Cells the log never visited are untouched, not zeroed.
- A zero or negative AFR target is not a target, and those samples are skipped.

Use the data filters to exclude what you do not want counted — up to
temperature, engine running, off idle. The summary line says how many cells were
suggested, how many were too thin, and the largest change.

Toggle *Show the new numbers* to switch between how far each cell moves and the
values themselves, then export the table as CSV to paste into your tuning app.
Reading the tune needs it embedded in the log, which TunerStudio does by default.

## Export

*Export ▾* in the toolbar. What it offers follows the mode you are in.

In log view:

| | |
|---|---|
| Plotted channels as CSV | just what is on the plot |
| All channels as CSV | everything the log carries |
| Plot as PNG | the plot as drawn, at 2× |

**Mark a span first and the CSV covers only that span** — the menu says which it
is about to write. Numbers are always invariant-culture, so a file written on a
machine with a comma decimal separator opens everywhere, and each value is the
shortest text that reads back as the same sample rather than the float's
rounding error printed to seventeen digits.

An exported CSV opens again in OpenLogViewer: the header and units rows are the
shape the delimited reader already detects, and a missing reading is an empty
cell, which it already decodes as one. Gaps in logging survive the round trip.

In histogram view:

| | |
|---|---|
| Table as CSV | the binned values, highest row first |
| Sample counts as CSV | how many samples landed in each cell |
| Table as PNG | the heat table as drawn, at 2× |

The table is written in the shape a tuning table has — X breakpoints across the
top, Y down the side, highest row first — so a block can be pasted straight into
a tuning app. Cells that were never visited are left empty rather than written
as zero, which would read as a measurement of nothing.

`--export <folder>` writes every export for the current mode without the
dialogs, for scripted use.

## Colour schemes

Pick one from the box at the top right. The choice is remembered in
`%APPDATA%\OpenLogViewer\settings.json`.

| Group | Schemes |
|---|---|
| Dark | Midnight (default), Graphite |
| Light | Daylight, Paper |
| Editor | Dracula, Nord, Solarized Dark, Solarized Light, Monokai, One Dark, Gruvbox Dark, Tokyo Night |
| High contrast | High Contrast Dark, High Contrast Light |

A scheme sets more than the chrome. It also carries its own **trace palette**,
and switching schemes re-picks the colours of everything plotted, because a
palette is only separable against the background it was chosen for.

Those palettes are not the upstream editor colours verbatim, and that is
deliberate. A syntax theme colours short spans of text that are never adjacent
and whose identity comes mostly from position; overlaid traces are neither — two
of them cross in the middle of the plot, and there is nothing but colour to tell
them apart. Every palette here was checked against its own background for
lightness range, chroma, contrast, and separation of neighbouring entries under
protanopia and deuteranopia. Each keeps its scheme's hues and relative
saturation and moves only in lightness. Colours are handed out in palette order
as channels are plotted, so what is on screen takes the entries checked to be
furthest apart.

The heat-table scales are derived from each scheme rather than listed: a
sequential ramp in one hue for magnitude, and a two-hue diverging scale about a
near-background neutral when a comparison channel is set. The derivation is
pinned by tests — a ramp that reversed direction, or two diverging arms that
converged at their extremes, would misreport the data rather than merely look
wrong.

Pass `--theme <id>` to start in a scheme for one run without changing the saved
preference; ids are the lower-case hyphenated names, e.g. `solarized-dark`.

## Requirements

- Windows
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build; the
  Windows Desktop runtime to run

## Build and run

```powershell
dotnet build OpenLogViewer.slnx -c Release
dotnet run --project src/OpenLogViewer.App -c Release
```

You can also pass a log directly, or drop one onto the window:

```powershell
dotnet run --project src/OpenLogViewer.App -c Release -- path\to\log.mlg
```

### Console dump

`OpenLogViewer.Dump` decodes a log and prints a summary. It doubles as the
regression check for the readers:

```powershell
dotnet run --project tools/OpenLogViewer.Dump -c Release -- path\to\log.mlg --channels
```

```
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

Add `--categories` to see how each channel was grouped, which is the quickest
way to check the classifier against a new firmware:

```
Common (17)
    AFR, AFR 1 Target, AFR load, Batt V
    Boost psi, CLT, Duty Cycle1, Dwell ...
Ignition (24)
    Ign load, Knock in, SPK: Base Spark Advance, SPK: Knock retard ...
```

### Tests

```powershell
dotnet test -c Release
```

Two suites:

- `OpenLogViewer.Tests` — the readers, the histogram, filters and tune axes.
  The MLG tests build synthetic log files in memory, so they cover the awkward
  cases — packed flag bytes, interleaved markers, scale/transform — without
  needing sample logs checked into the repository.
- `OpenLogViewer.App.Tests` — the view model, driven end to end: write a log,
  open it, and exercise the channel list, presets, filters and histogram the way
  the UI does. The preset and filter stores are injected with temporary paths so
  tests never touch real user settings.

The viewer can also render itself to a PNG, which is how the screenshot above is
produced (capturing from another process is unreliable under DWM composition):

```powershell
OpenLogViewer.App.exe path\to\log.mlg --screenshot out.png
```

Add `--pointer 0.42,0.55` (fractions of the plot area) to place the cursor first,
so the hover readout appears in the capture.

## Layout

```
src/OpenLogViewer.App/AppIcon.ico  application icon (see below)
src/OpenLogViewer.Core    format readers and the log model (no UI dependency)
src/OpenLogViewer.App     WPF viewer
tools/OpenLogViewer.Dump  console decoder / regression harness
tests/OpenLogViewer.Tests xunit tests
docs/mlg-format.md        the MLG binary format
```

`OpenLogViewer.Core` targets plain `net10.0` and has no WPF reference, so it can
be reused from a console tool, a test, or a non-Windows host.

## Icon and logo

`AppIcon.ico` carries ten sizes (16 → 256) with **two different pieces of art**:

- **64 px and above** — the full illustration ([`docs/logo.png`](docs/logo.png)),
  which is also the project logo above
- **Below 64 px** — a drawn mark ([`docs/mark.png`](docs/mark.png)): an RPM trace
  climbing through gear shifts, with a boost trace beneath it

The photographic artwork is unreadable at taskbar size — at 16 px a detailed
scene is just a smudge — so the small entries get a purpose-drawn glyph instead.
The mark is rendered natively at each size rather than downscaled, so its strokes
stay crisp, and it sits on a mid-dark slate tile that holds its own against both
light and dark taskbars (the near-black illustration does not).

## On provenance

Original work. This project is not derived from any existing application, and no
proprietary software was decompiled, disassembled, or otherwise reverse
engineered to build it.

The MLG format was reconstructed by reading sample `.mlg` files directly. The
container is self-describing — it carries its own channel names, data types,
units and scaling factors — so the layout was recovered by inspecting file
headers, testing candidate record strides against physical plausibility
(battery voltage sits at 12–14.5 V; coolant temperature reaches 160–200 °F),
and confirming that the resulting record count divides the file exactly. That
process is documented in [`docs/mlg-format.md`](docs/mlg-format.md) so it can be
independently checked.

## License

MIT — see [LICENSE](LICENSE).

This is a tool for engine tuning. It only reads log files and never writes to an
ECU, but tuning decisions made with it are yours; the authors accept no
liability for engine damage.
