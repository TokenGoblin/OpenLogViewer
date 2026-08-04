<p align="center">
  <img src="docs/logo.png" alt="OpenLogViewer" width="440">
</p>

<h1 align="center">OpenLogViewer</h1>

<p align="center">
  A native Windows datalog viewer for MegaSquirt, TunerStudio and other ECU logs.<br>
  Built on .NET 10 and WPF. One dependency, `System.IO.Ports`, for the live ECU
connection — Microsoft-published and part of .NET, but a package rather than in
the framework.
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
- **Live connection** to MegaSquirt, rusEFI, Speeduino, MaxxECU, and any OBD2
  vehicle through an ELM327 — see below
- **Export** — see below
- One dependency: `System.IO.Ports`, for the live connection

## Where files go

```
C:\Users\<you>\OpenLogViewer\
    Logs\      live recordings, named for when they were taken
    Exports\   where Export starts
```

One folder, three levels down, named after the app. *Export ▾ → Open the
folder* takes you there; *Change the folder…* moves it, and the choice is
remembered.

Deliberately **not** "My Documents". That is redirected into OneDrive on most
machines, which buries recordings a couple of levels deeper and uploads every
one of them *while it is still being written* — a long session is tens of
megabytes of continuous sync over whatever connection the car happens to be
near. The user profile is not redirected.

Settings are separate, under `%APPDATA%\OpenLogViewer\` — `settings.json`,
`presets.json`, `filters.json`, `math.json`. Those belong to the app; the
folder above belongs to you. Nothing is ever written next to the executable, so
the app is content installed read-only under Program Files.

## Live connection

*Connect ▾* in the toolbar lists the serial ports. Pick one and OpenLogViewer
asks the ECU what it is, finds the INI that matches, and starts reading and
recording. The button becomes *Disconnect*.

A live session is an ordinary log. The sidebar, filters, calculated channels,
the heat table and VE Calibration all work on it exactly as they do on a file,
and channels take the names your recorded logs use — so a preset or a filter
saved against a file applies to the ECU too.

```
● COM9 · MS3 Format 0569.00 · 15.7 Hz
```

The toolbar says what is connected: port, firmware, and the rate. The dot means
it is recording. Hover it for the full picture — build string, the INI matched,
channel count, and the file being written. Retries only appear once there are
any.

**The INI is matched to the signature the ECU reports, and a session is refused
when none matches.** This is the one part worth understanding. Firmware versions
move channels around inside the realtime block, so decoding with the wrong INI
does not fail — it reads every channel from the wrong offset and returns numbers
that look entirely reasonable. Even adjacent versions count as no match.

TunerStudio keeps INIs under `.efiAnalytics/TunerStudio/config/ecuDef` and in
each project folder; both are searched. **Open the tune before connecting** if
you want the channels the firmware derives from tune settings — duty cycle
divides by the cylinder count, and that does not come over the wire.

**Recording is yours to start and stop.** A **Record** button sits next to
*Connect* whenever a session is live. You choose the moment, the name and the
folder; the next recording is offered wherever the last one went, and the
suggested name carries the ECU and the time. Each recording is a log in its own
right — its clock starts where you pressed record, not where the session began,
so it opens without twenty minutes of nothing in front of it. One session can
produce as many files as you like.

**Connecting does not record on its own.** A session is opened to check a link,
read a gauge or watch a change far more often than to capture anything, and
recording every one of those buries the run that mattered among the ones that did
not. *Tools ▸ Record as soon as I connect* puts it back to recording from the
moment you connect, and is remembered.

The state is stated rather than left to be inferred, because what this default
costs is a run somebody meant to capture: the toolbar button reads `● Record…` or
`■ Stop recording`, the status bar reads `REC 1,204 rows` or `not recording`, and
connecting says which of the two it is doing.

Within a recording, writing is continuous rather than saved at the end. A session
ends by a pulled cable at least as often as by being stopped, so every row is
flushed as it arrives and the file is complete the moment you stop.

**Losing the link does not end the session.** Key off and key on is normal, so a
lost link is waited on for a minute — the indicator goes hollow and amber — and
the session carries straight on into the same recording when the ECU comes back.

The plot follows the newest data and stops following as soon as you zoom or pan,
because from then on you are reading history. *Reset zoom* goes back to
watching. **Hide unused** is on by default, so on a bench with the engine off
almost everything is hidden — everything is still being recorded.

**Reading is what a session does; writing takes asking for.** Connecting sends
only the commands that ask what the firmware is, read the realtime page, and
read the settings — the same things TunerStudio reads continuously. Nothing is
written unless you edit a table and press the button, and VE Calibration
suggests a table rather than applying one. See *Editing a table*, below.

### Editing a table

*Calibration* shows the tables as the ECU holds them, read off the controller
rather than from a saved file. They can be changed and sent back.

Click or drag to pick a block of cells; arrows move, shift extends. Then:

| | |
|---|---|
| `+` `−` | nudge by the firmware's own smallest step (shift: ten of them) |
| `PgUp` `PgDn` | scale by 1% (shift: 5%) |
| `Esc` | put the selection back to what the ECU said |

Scaling is there because it is how tuning is actually done — a region reading
four per cent lean is corrected by adding four per cent to it, not by typing
sixteen numbers.

**A changed cell is outlined, and the header counts them.** The shading still
says what the value is; the outline says it is not what the ECU holds. Nothing
is sent until you press *Send to ECU*, which says how many cells it is about to
change — a table scaled by 5% when one cell was meant is 256 changes, and it
looks identical to one change until it is counted.

Values are held to the range the firmware declares, which is far tighter than
the storage allows: an ignition table kept as a signed 16-bit tenth of a degree
would accept ±3,276° as far as the encoding cares, while MS2Extra declares −10
to 90.

**Send and Burn are separate, because they are separate on the ECU.** A write
lands in working memory and takes effect immediately on a running engine — and
is forgotten at the next power cycle, so a change that turns out to be wrong is
undone by turning the key off. A burn commits it to flash and is permanent; do
it with the engine stopped, since the ECU pauses while it writes. Every write is
read back and compared before it is called done.

### OBD2 through an ELM327

Any standard vehicle, with no definition file and no aftermarket ECU. This is
the one connection here that needs nothing set up in advance: SAE J1979 fixes
what every parameter means, so the numbering, the scaling and the units are the
same on every compliant car, and the car itself reports which parameters it
answers to. Plug in a dongle on a vehicle nobody has ever tried this on and you
get named, scaled channels.

Dongles that advertise as one — `OBDII`, `OBDLink`, `ELM327`, `Vgate` and the
rest — are recognised and connected as adapters automatically. Generic ones that
Windows only describes as a `USB-SERIAL CH340` are indistinguishable from a
tuning cable until something talks to them, and the two want opposite opening
moves, so those go through **Connect ▾ → Connect as an OBD2 adapter**.

**Bluetooth LE adapters work too, and most cheap ones now are.** BLE has no
serial port profile, so these never become a COM port however long you wait —
which is how a perfectly good dongle comes to look broken. They carry the same
ASCII ELM327 conversation over two GATT characteristics instead, so they are
listed in the same menu as the ports with `(Bluetooth LE)` after the name. There
is no standard for which service to use, so the known ones are tried in turn
(`0xFFF0`, `0xAE00`, `0xFFE0`, Nordic UART) and **each is proved by asking it
something before being used** — the adapter this was verified against publishes
two and answers on only one of them.

On a wired adapter the speed is found rather than assumed. A genuine ELM327
ships at 38,400 and clones ship at whatever the batch was built with, so 38,400,
115,200, 9,600 and 500,000 are tried in that order; a Bluetooth adapter ignores
the setting entirely. A wrong speed is told apart from a key left out, because
the two need different things done about them.

**It is slow, and that is the protocol rather than this.** Every other ECU here
hands over its whole realtime block in one exchange; OBD2 has no such thing, so
each parameter is a separate request and a separate wait. Measured at 2.2 Hz on
a live car against forty on a tuning cable — fine for watching a car, no use for
catching a misfire. The parameters a needle follows (RPM, speed, throttle, load,
MAP) are asked for every round and the rest take turns, so the headline gauges
stay live while the fuel level updates when it gets to it.

Dials are drawn to the standard's own ranges, with two deliberate exceptions
worth knowing: no gauge has a warning or danger band, because OBD2 describes
what a value is and never what a safe one would be; and the rev counter is drawn
to 8,000 rather than to the 16,383.75 the encoding permits, since there is no
way to ask a car for its redline and a dial drawn to the counter's ceiling
leaves every real reading in the first quarter.

A standard vehicle has no tune to read, so Calibration is not available for it.

**Fault codes.** *Tools ▸ Fault codes…*, once connected. All three of the
standard's lists are read, because they are three different statements about the
car: **confirmed** codes are what lit the lamp, **pending** ones were seen once
and the car does not yet believe them — most monitors want the same fault on two
consecutive drive cycles — and **permanent** ones cannot be erased by anything
but the controller, which is what they are for.

Each code carries the SAE definition where it has one. Where it does not, the
window says why rather than guessing: the manufacturer-specific ranges are the
maker's to assign, so P1131 means one thing on a Ford and something unrelated on
a Toyota, and a plausible description of the wrong one is how somebody ends up
buying a sensor they did not need.

**Erasing does not fix anything, and costs more than the codes.** Mode 04 clears
the freeze frame — the one record of what the engine was doing at the moment the
fault occurred, and the most useful thing there is for an intermittent — along
with the oxygen sensor results and the readiness monitors. A car cleared this
morning cannot pass an emissions test this afternoon whatever its condition,
because it no longer has evidence that its monitors ever ran. The confirmation
says all of that, permanent codes are read back afterwards and reported, and most
cars refuse the request with the engine running.

On an OBD2 connection the **Calibration** tab shows this too, and scans the first
time you switch to it. There is no tune on a standard vehicle to put there
instead, and diagnostics is the thing OBD2 offers that is about the car rather
than about this moment.

A scan can be run while the session is polling. The adapter takes one command at
a time, so the gauges stop for a second or two while the car is being asked.

Verified on a live vehicle through a Bluetooth LE `ELM327 v1.5`: 24 parameters
at 2.19 Hz, connected in five seconds. The decode cross-checks — with the engine
stopped, MAP and barometric pressure read 86 kPa apiece, which they only do if
both formulas are right.

Verified again with the engine running, which tests things a parked car cannot:
25 parameters at 2.7 Hz over an OBDLink r2.6, none of them falling silent. MAP
read 20 kPa at idle and 87 kPa on a throttle blip against a barometric 86 — an
unloaded blip brings manifold pressure up to atmospheric and no further, so that
is the whole range checked rather than the single point the stopped-engine test
covers. Every fuel trim reading landed on an exact raw byte, which fixes the
scale, and long-term trim sat at +1.56% — two counts off the 128 that means "no
correction" — which fixes the offset, since a wrong one would leave a healthy
engine showing a large standing correction. Lambda oscillated 0.97 to 1.22 about
1.00, which is closed loop doing what closed loop does.

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

### Where the tune comes from

By default, the one stored inside the log — TunerStudio embeds it in an MLG,
and that copy is the one that was actually running when the log was recorded.

*Open tune…* sits beside *Open log…* in the toolbar, and again next to **Axis
breakpoints** where it takes effect; you can also drop a `.msq` on the window.

Which tune is in use is shown on the right of the toolbar — *from the log*, the
filename, or *none*. It turns amber if the opened tune does not match the log.
Right-click *Open tune…* to go back to the log's own.

Opening a tune by hand is what you need for a `.msl` or `.csv` log, which
carries no tune at all. **For an MLG it is usually the wrong thing to do.** VE
Analyze scales the numbers that produced the logged AFR; feed it a table you
have edited since the drive and it will scale numbers the engine never ran, and
suggest a table wrong by however much the tune moved in between. If the opened
tune's fuel table differs from the log's, the sidebar says so.

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

### Installer

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add --global WixToolset.UI.wixext/5.0.2

installer\build.ps1
```

Produces `installer\out\OpenLogViewer-<version>-win-x64.msi` — about 54 MB.

**Self-contained**, so nothing has to be installed first. The people this is for
plug a laptop into a car, often in a garage with no internet, and "download a
60 MB runtime before you can open your log" is the wrong thing to say at that
moment. It costs about 130 MB over a framework-dependent build, which is
unremarkable for a download and decisive in a workshop. Trimming is not
available — WPF is not trim-safe — so that size is near its floor.

The version comes from the application's own `<Version>` unless you pass
`-Version`, so the installer and the thing it installs cannot disagree. Use
three parts: Windows Installer ignores the fourth when deciding whether one
build supersedes another, and a four-part version means releases that silently
refuse to upgrade each other.

**WiX 5 rather than 6 or 7**, which are gated behind the Open Source Maintenance
Fee and refuse to run without accepting its licence.

`.mlg`, `.msl` and `.MaxxECU-Zip-log` are registered under `OpenWithProgids`
only. OpenLogViewer appears in **Open with** and never takes the double-click —
anyone running this most likely has TunerStudio installed, and those are its
files.

Uninstalling removes the program and leaves `%USERPROFILE%\OpenLogViewer` alone,
because those are your recordings.

**Not signed.** Windows shows a SmartScreen warning on first run until it is,
which needs a code-signing certificate rather than a code change.

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
