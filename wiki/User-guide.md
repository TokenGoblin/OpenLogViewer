# User guide

Everyday operation: reading a recorded log, finding things in it, and getting
results back out.

- [The three ways to read a log](#the-three-ways-to-read-a-log)
- [The channel list](#the-channel-list)
- [Presets](#presets)
- [Reading the plot](#reading-the-plot)
- [Per-channel appearance](#per-channel-appearance)
- [Finding a moment](#finding-a-moment)
- [Comparing two logs](#comparing-two-logs)
- [Insights](#insights)
- [Estimating power](#estimating-power)
- [Calculators](#calculators)
- [Units](#units)
- [Colour schemes](#colour-schemes)
- [Export](#export)
- [Supported log formats](#supported-log-formats)

---

## The three ways to read a log

A **channel** is one recorded quantity — RPM, coolant temperature, air/fuel
ratio. A log is a set of channels sampled together over time.

The same samples and the same settings can be read three ways. Switch between
them in the toolbar or under **View**.

| View | What it shows | The question it answers |
| --- | --- | --- |
| **Plot** | Channels drawn against time | What happened, and when |
| **Histogram** | Samples binned into a table of two axes | What a region of the map averaged |
| **Scatter** | Every sample at its own X and Y, coloured by a third channel | How much the samples behind an average disagreed |

Histogram and Scatter have their own page: [Histogram and
scatter](Histogram-and-scatter).

## The channel list

The sidebar on the left lists every channel in the log. Tick one to plot it.

| Control | What it does |
| --- | --- |
| Search box | Matches on channel name, units or category |
| **Common** | Plots the set most logs are read for, in one click |
| **All** / **None** | Plot everything, or nothing |
| Sort chips | **Category**, **A–Z** or **Plotted** |
| **Hide unused** | Hides channels that never change. **On by default** |
| Right-click a row | Jump to an extreme, plot only that channel, or set its colour, scale and smoothing |

Channels are grouped by system — Common, Engine, Air & boost, Fuel, Ignition,
Temperature, Idle, Electrical, Diagnostics — in collapsible sections. The
grouping is derived from the channel's name and units; see
[Firmware definitions and channels](Firmware-definitions-and-channels) for how.

**Hide unused** deserves a note. Logs routinely declare channels that never
move — 98 of 179 in one sample log — and hiding them is what makes the rest
findable. Hidden channels are still recorded and still exported; only the list
is filtered.

Calculated channels are marked **ƒ**. See [Calculated
channels](Calculated-channels).

## Presets

A **preset** is a named set of plotted channels.

**To save one:** plot the channels you want, click **+ Save** above the list,
and name it.

**Expected result:** the preset appears as a chip above the list. Clicking it
restores exactly that selection. Right-click a chip to overwrite or delete it.

Presets are matched **by channel name**, not by log, so one saved against a
recorded file applies to any other log — or to a live session — that carries
those names. They are stored as readable JSON in
`%APPDATA%\OpenLogViewer\presets.json`, which you can hand-edit or copy between
machines.

## Reading the plot

### Hover

Move the pointer over the plot.

- Every channel row shows its value at that moment.
- The trace nearest the pointer thickens and its row highlights.
- A card gives that channel's value, its maximum and its minimum — **and the
  moment each occurred**.

### Zoom, pan, and marking

| Gesture | What it does |
| --- | --- |
| Scroll | Zoom at the pointer |
| Drag | Pan |
| Double-click | Fit the whole log |
| Shift + drag | Mark a span |
| Click | Clear the marked span |
| **View ▸ Reset zoom** | Back to the whole log |

Marking a span changes what the sidebar reports: every row switches to
`min … max` and the average over that span, and the hover card describes the
span rather than the whole log. A marked span also restricts a CSV export, and
outlines the histogram cells that span passed through.

### Jumping to an extreme

Three ways, all of which keep the current zoom and plot the channel first if it
was not already showing:

- The **▲** and **▼** buttons on a channel row
- Right-click a channel row ▸ **Jump to maximum** / **Jump to minimum**
- Click the max or min line inside the hover card

### Overlaid or stacked

| Mode | Each trace is | Best for |
| --- | --- | --- |
| **Overlaid traces** | Scaled to its own range, all sharing the plot | Comparing timing between channels |
| **Stacked traces** | Given its own horizontal strip | More than about four channels |

Toggle in the toolbar or under **View**.

### Two things the plot does that are not obvious

**Gaps in logging are drawn as gaps.** A paused-and-resumed log leaves a hole,
and a straight line across an eight-minute pause would read as steady data that
was never recorded. The pen lifts when the step between samples exceeds ten
times the log's median sample interval.

**Steady channels are drawn as steady.** A sensor holding almost still would
otherwise have its last decimal place stretched to fill the lane — a pressure
holding 12.0 within a tenth drawn as a wall of noise. Turn this off with **View
▸ Draw steady channels as steady** when a small drift is exactly what you are
chasing.

## Per-channel appearance

Right-click any channel row. Everything in this menu is remembered **by channel
name** in `%APPDATA%\OpenLogViewer\channels.json`, so a choice made on one log
applies to every other log carrying that channel.

| Menu item | What it does |
| --- | --- |
| **Jump to maximum** / **Jump to minimum** | Frames that channel's extreme, keeping the current zoom |
| **Plot only this channel** | Unticks everything else and plots this one alone |
| **Colour** | Pins a colour from the current scheme's palette |
| **Fixed scale…** | Draws the channel over a range you name instead of its own |
| **Smoothing** | None, Light, Medium or Strong. **Drawing only** |
| **Back to automatic** | Clears the colour, the scale and the smoothing at once |

> **NOTICE:** `channels.json` holds settings for up to 500 channels. Past that
> the application says there is no room left rather than quietly discarding an
> older one; clear a channel you no longer pin and try again.

### Fixed scale

Auto-scaling every trace to its own range is what lets a dozen channels in
different units share one plot, and it costs comparability: the same channel is
drawn over a different range in every log, and in the same log before and after
a filter. Two runs cannot then be read against each other by eye.

**To pin a scale:** right-click the channel ▸ **Fixed scale…**, and give a low
and a high bound. The editor opens seeded with the range the channel is drawn
over now.

**Expected result:** the channel is always drawn over that range. In stacked
lanes the axis labels report the range the lane is actually drawn over, so a
pinned trace says what it is scaled to.

The boxes take and show their bounds in the units the list is currently showing,
and say which. The pin itself is stored in the log's own units, so switching
between metric and imperial redraws the labels without moving the range.

A pinned scale is used exactly as given — the steady-channel floor described
above is not applied on top of it.

### Pinned colour

**To pin a colour:** right-click the channel ▸ **Colour**, and pick from the
current scheme's palette.

> **NOTICE:** A pinned colour opts out of re-checking. Trace colours are
> normally re-picked whenever the colour scheme changes, because a palette is
> only separable against the background it was chosen for. A pinned colour is
> not re-picked, so one chosen on a dark scheme may read poorly on a light one.

An entry a pinned channel holds is not handed out to another trace, so two
traces never share a colour.

### Smoothing

Quietens a noisy trace so it can be read.

**To smooth a channel:** right-click it ▸ **Smoothing**, and pick a level.

| Level | Window | What it is for |
| --- | ---: | --- |
| **None** (default) | — | As logged |
| **Light** | 5 samples | Takes the fuzz off without moving anything |
| **Medium** | 15 samples | A noisy sensor becomes a readable line |
| **Strong** | 51 samples | The shape only, for a channel that is mostly noise |

**Expected result:** the trace is drawn smoothed and the channel row reads
`smoothed · median of 15`. The hover readout follows the drawn line.

> **WARNING:** **Smoothing is a way of drawing, not a way of measuring.** A
> smoothed AFR hides exactly the single-sample lean excursion that damages a
> piston. Do not use a smoothed trace to decide an engine is safe.

**Nothing that judges the engine reads through it.** Insights, VE calibration,
the histogram, the scatter, the channel statistics and every export all take the
channel **as logged**. What smoothing changes is the line on the plot and the
figure read off it, which is a question about eyesight rather than about the
engine.

Three details that matter if you are reading a smoothed trace closely:

- **It is a median, not an average.** Sensor noise arrives as spikes, and a mean
  smears each spike across the whole window — one bad sample in fifteen moves
  the line for fifteen samples, which is worse than the spike. A median throws
  the spike away and keeps the edges, so a genuine step from one pressure to
  another survives where a mean would round it off.
- **The window is counted in samples, not seconds.** Noise of this kind is per
  reading, whether they arrive at 1 Hz or 50. A window stated in time would
  smooth nothing on a slow log and destroy a fast one. The same level therefore
  covers 5 s on a 1 Hz OBD2 link and 0.1 s at 50 Hz.
- **The ends are not padded.** The window shrinks at the first and last samples
  rather than inventing data to fill it. A missing reading stays missing, so the
  pen still lifts across a gap; missing readings inside a window are passed over,
  which thins the evidence rather than poisoning it.

**Back to automatic** clears the colour, the scale and the smoothing at once.

**Verified on a live Speeduino.** With medium smoothing pinned on a channel, its
trace was drawn across 99.3 % of a 22-second session. Against a build without the
fix that keeps the smoothed copy in step with a growing live log, the same trace
covered 0.8 % — it stopped at the first poll and the pen stayed lifted for the
rest of the session.

## Finding a moment

**Ctrl+F**, or **View ▸ Find in the log…**.

Type a condition. Every stretch of the log that met it is shaded, and the search
steps through them.

```text
RPM > 4500 && TPS > 80
AFR > 16 && MAP > 150
CLT > 210
```

| Key | What it does |
| --- | --- |
| Enter | Run the search, then step forward |
| Shift + Enter | Step back |
| Esc | Close the search bar |

The syntax is the same as a [calculated channel](Calculated-channels),
deliberately: a condition that proves useful can be pasted into a calculated
channel or a filter without translation.

**What this adds over a filter is *where*.** A filter says which samples to
count and throws the rest away; a search says which moments to go and look at,
and leaves the log alone. Filters still apply — they say which part of the drive
is under consideration.

Two behaviours worth knowing:

- **Consecutive matches are one finding, not fifty.** A signal sitting near its
  threshold crosses it repeatedly — RPM wandering about 4,000 against
  `RPM > 4000` alternates true and false every few samples — so a brief dip
  below is bridged.
- **A sample where a named channel has no reading is counted as *could not be
  judged*,** separately from the misses. A comparison against a reading that was
  never taken is unanswerable rather than false.

## Comparing two logs

The comparison a tuner actually makes: change something, drive it again, and
find out what moved.

**To compare:**

1. Open the first log.
2. **File ▸ Compare against another log…** and choose the second.

**Expected result:** the application reports how many channels the two logs
share, and how many are unique to each.

| Command | What it does |
| --- | --- |
| **File ▸ Compare against another log…** | Opens a second log alongside this one |
| **File ▸ Show the difference** | Subtracts the second log's table from this one, cell by cell |
| **File ▸ Stop comparing** | Closes the second log |

**Show the difference** applies to the histogram table. Where the first table
holds 78 % VE and the second holds 81 %, the difference cell reads +3. The
diverging colour scale is centred on zero, so which way a cell moved reads at a
glance and the size of the move reads as lightness.

**Limitations:**

- Channels are matched **by name**. Two logs from different firmware, or one
  exported with renamed columns, may share nothing. The application says so
  rather than showing an empty result.
- The two logs are not the same length and did not start at the same moment.
  They are read against each other by operating point, not by wall-clock time.

## Insights

**◈ Insights** in the toolbar. A findings window that reads the log arithmetically. **Every finding is a
calculation on the samples rather than a rule of thumb.**

The distinction matters: a tuner can already see the traces. What they cannot
see is that the mixture under boost is four tenths lean of target with a
standard error of six hundredths — a real difference — while the same four
tenths at idle over nine samples is not.

Findings are ranked:

| Level | Meaning |
| --- | --- |
| **Warning** | Something that damages engines |
| **Watch** | Worth looking at before the next drive |
| **Note** | Nothing to act on, but worth knowing |
| **Good** | Checked, and as it should be |
| **Unanswered** | The log cannot answer this — usually a channel it does not carry |

Each finding carries the numbers behind it and how many samples it rests on, so
you can disagree with the conclusion without taking the measurement on trust.

## Estimating power

**Tools ▸ Estimate power…**, with a log open.

This adds calculated channels that estimate crank power from the log. It is an
estimate from fuel flow and assumed efficiency, not a measurement.

> **NOTICE:** These figures are not dynamometer results. The largest unknown is
> brake specific fuel consumption (BSFC), which the calculation must assume. Use
> them to compare one run against another under the same assumptions, not as an
> absolute power figure.

Defaults, all of which you can change in the dialog:

| Input | Default | Units | Notes |
| --- | ---: | --- | --- |
| Displacement | 2.0 | litres | |
| Cylinders | 4 | | |
| Fuel | Petrol | | |
| Volumetric efficiency | 95 | % | Used only for logs that do not record VE |
| Lambda | 0.85 | λ | Used only where the log carries no wideband |
| Injector flow | 550 | cc/min | Static flow, one injector |
| Injector rated pressure | 300 | kPa | The differential pressure that rating was taken at |
| Injector dead time | 1.0 | ms | Subtracted from the logged pulse width |

Dead time is worth getting roughly right: at 7,000 rpm one millisecond is six
per cent of the cycle, so leaving it out overstates the fuel and therefore the
power.

## Calculators

**Tools ▸ Calculators…** — the arithmetic a tuner otherwise keeps a phone open
for. Fifteen calculators in six groups:

| Group | Calculators |
| --- | --- |
| Plan a build | Engine recipe |
| Air & boost | Boost, Pressure ratio, Turbo sizing, Airflow, Intercooling |
| Fuel | Injectors, Fuel pump, Lambda, Octane |
| Engine | Engine, Runners & headers |
| Drivetrain | Gearing, Drag strip |
| Running costs | Fuel economy |

Each calculator states what it is *not* modelling. The drag-strip figures, for
example, are correlations fitted to real runs rather than physics — there is no
term for traction, gearing, the air or the driver. That statement is what
decides whether an answer is any use to you.

## Units

**View ▸ Units.**

| Option | What it shows |
| --- | --- |
| **As reported** (default) | Exactly what the ECU said, converted not at all |
| **Metric** | Converted to metric where the conversion is exact |
| **Imperial** | Converted to imperial where the conversion is exact |

**As reported** is the default because it is the only setting that cannot be
wrong. An ECU reports what it reports: OBD2 is metric by standard, a MegaSquirt
is whatever its tune was set to, and a car with a mile-marked speedometer will
happily report km/h all day.

> **NOTICE:** This is a display setting only. Nothing here changes what is
> recorded, so a log is always written in the units its ECU used and reopening
> it later cannot double-convert.

Only families where the conversion is exact and the unit is unambiguous are
converted; anything else is shown as reported.

## Colour schemes

Pick one from the box at the top right, or under **View ▸ Theme**. The choice is
remembered in `%APPDATA%\OpenLogViewer\settings.json`.

| Group | Schemes |
| --- | --- |
| Dark | Midnight (default), Graphite |
| Light | Daylight, Paper |
| Editor | Dracula, Nord, Solarized Dark, Solarized Light, Monokai, One Dark, Gruvbox Dark, Tokyo Night |
| High contrast | High Contrast Dark, High Contrast Light |

A scheme sets more than the window chrome. It carries its own **trace palette**,
and switching schemes re-picks the colours of everything plotted, because a
palette is only separable against the background it was chosen for.

The editor palettes are not the upstream syntax colours verbatim. A syntax theme
colours short spans of text that are never adjacent; overlaid traces cross in
the middle of the plot with nothing but colour to tell them apart. Every palette
here was checked against its own background for lightness range, chroma,
contrast, and separation of adjacent entries under protanopia and deuteranopia.

`--theme <id>` starts in a scheme for one run without changing the saved
preference. Ids are the lower-case hyphenated names, for example
`solarized-dark`. See [Command line](Command-line).

## Export

**File ▸ Export.** What is offered follows the view you are in.

### From the plot

| Export | What it writes |
| --- | --- |
| Plotted channels as CSV | Just what is on the plot |
| All channels as CSV | Everything the log carries |
| Plot as PNG | The plot as drawn, at 2× |

**Mark a span first and the CSV covers only that span.** The menu says which it
is about to write.

### From the histogram

| Export | What it writes |
| --- | --- |
| Table as CSV | The binned values, highest row first |
| Sample counts as CSV | How many samples landed in each cell |
| Table as PNG | The heat table as drawn, at 2× |

### From the scatter

| Export | What it writes |
| --- | --- |
| Plotted points as CSV | One row per sample, with its index in the log |
| Scatter as PNG | The scatter as drawn, at 2× |

### What the CSV guarantees

- **Invariant culture.** A file written on a machine with a comma decimal
  separator opens everywhere.
- **Shortest round-tripping text.** Each value is the shortest text that reads
  back as the same sample, rather than a float's rounding error printed to
  seventeen digits.
- **A missing reading is an empty cell,** so gaps in logging survive the round
  trip.
- **An exported CSV opens again in OpenLogViewer.** The header and units rows
  are the shape the delimited reader already detects.

The histogram table is written in the shape a tuning table has — X breakpoints
across the top, Y down the side, highest row first — so a block can be pasted
straight into a tuning application. Cells never visited are left **empty**
rather than written as zero, which would read as a measurement of nothing.

Exports go to `%USERPROFILE%\OpenLogViewer\Exports` by default. See
[Configuration ▸ Where files go](Configuration#where-files-go).

`--export <folder>` writes every export for the current view without the
dialogs. See [Command line](Command-line).

## Supported log formats

| Source | Format | Status |
| --- | --- | --- |
| MegaSquirt / TunerStudio | `.mlg` binary | Verified against MS2 and MS3 logs, including markers and packed flag bytes |
| MegaSquirt / TunerStudio | `.msl` text | Verified against 43 real logs, MS3 1.3.3 through 3.3 |
| rusEFI | `.mlg`, `.msl` | Same `MLVLG` container and text layout |
| MaxxECU | `.MaxxECU-Zip-log` | Zipped log, read directly |
| MoTeC, Haltech, Link, AEM, ECUMaster, Holley, HP Tuners, Speeduino | CSV / TSV export | Generic delimited-text reader |
| Anything else | Delimited text | Read if it has a header row and numeric columns |

Nothing is assumed from the file extension — the content is examined instead.
The text reader works out the following for itself:

| Detected | Handled |
| --- | --- |
| Delimiter | Tab, comma, semicolon or pipe, chosen by which splits the file most consistently |
| Decimal separator | Point or comma (`1234,5`, as European-locale exports write it) |
| Quoting | Quoted fields containing the delimiter (RFC 4180) |
| Units | From a dedicated units row, or bracketed in the header: `MAP (kPa)`, `RPM [rpm]`, `CLT {degC}` |
| Encoding | UTF-8 or Latin-1. Older TunerStudio exports are ISO-8859-1, where `°F` is a single byte that is not valid UTF-8 |
| Time base | Seconds, milliseconds or minutes; from a wall-clock timestamp column; or synthesised from the sample index when there is no usable time column |
| Duplicate names | Disambiguated by units — MS3 emits `Fuel Consumption` twice, in GPH and l/hr |

A log that starts at t = 2178 s, or at a negative time, is read correctly.

The binary format is documented in [The MLG log format](MLG-log-format). If a log
from an ECU not listed above does not open, the format is usually easy to add —
the delimited reader is one file.
