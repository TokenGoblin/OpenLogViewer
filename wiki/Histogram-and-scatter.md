# Histogram and scatter

Two ways of reading a log by operating point rather than by time. They share the
same three channels, the same filters and the same side panel; only the question
differs.

- [Which one to use](#which-one-to-use)
- [The histogram (heat table)](#the-histogram-heat-table)
- [Axis breakpoints from the tune](#axis-breakpoints-from-the-tune)
- [Data filters](#data-filters)
- [Tracing a cell back to the log](#tracing-a-cell-back-to-the-log)
- [Scatter](#scatter)
- [Why the colours are what they are](#why-the-colours-are-what-they-are)
- [Reference](#reference)

---

## Which one to use

| | Histogram | Scatter |
| --- | --- | --- |
| What it draws | One mark per cell, averaging the samples in it | One mark per display block, averaging only what physically overlaps |
| The question | What did this region of the map average? | Did the samples behind that average agree with each other? |
| Resolution | Your chosen bins, up to 40 × 40 | About one mark per 3 × 3 pixels |
| Use it to | Edit a tuning table against the drive | See structure and spread a table has averaged away |

A cell reading dead on target looks identical whether it was measured twelve
times at target or six times rich and six times lean. That is what the scatter
is for.

## The histogram (heat table)

Switch to **Histogram** in the toolbar, or **View ▸ Histogram**.

### What it does

Bins the log into a table with the same shape as a tuning table in the ECU, so a
drive can be read against the tune it came from.

### How to build one

1. Pick the **X** channel (across the top) — usually RPM.
2. Pick the **Y** channel (down the side) — usually MAP, or load.
3. Pick the value channel — AFR, VE, timing, whatever you are reading.
4. Choose how each cell reduces its samples: **Mean**, **Min**, **Max** or
   **Count**.

**Expected result:** a shaded table. Hovering a cell reports the bin it covers,
the value, and how many samples back it.

### Controls

| Control | Default | What it does |
| --- | --- | --- |
| Columns | 16 | Bins across. 2 to 40 |
| Rows | 16 | Bins down. 2 to 40 |
| Statistic | **Mean** | Mean, Min, Max or Count |
| **Only the zoomed time range** | Off | Restricts the table to the window you zoomed to on the plot |
| **Colour by sample count** | Off | Re-shades by how much data backs each cell |
| **Compare against** | None | Subtracts a target channel, turning "what did it read" into "how far off is it" |
| **Axis breakpoints** | Uniform | Uses the tune's own axes instead — see below |

**Only the zoomed time range** is how you build a table from a single pull
rather than a whole drive.

**Colour by sample count** is the quick way to see which parts of the table the
drive actually exercised. A table that looks evenly populated across the whole
map usually means the axes are wrong.

## Axis breakpoints from the tune

### Why this matters

Uniform bins spanning the observed range never line up with the table you are
actually editing, because ECU axes are not uniform — they are tight at idle and
wide at the top.

Every `.mlg` embeds the `.msq` tune it was recorded with, so the tune's own axes
can be read straight out of the log.

### How to use it

Pick one under **Axis breakpoints**.

**Expected result:** the table is binned onto exactly those breakpoints, cell for
cell against the table in TunerStudio. VE, spark and AFR-target tables are
offered when the tune has them.

```text
VE table 1  (16×16)
  frpm_table1 [RPM]: 500 800 1100 1400 1800 2200 2600 3000 3500 4000 4300 4700 5200 5700 6100 6500
  fmap_table1 [kPa]: 30 40 50 60 70 80 90 100 120 140 160 180 200 230 260 300
```

Samples are assigned to the **nearest** breakpoint, which is how a value between
two rows is attributed in a tuning table. Values beyond either end fall to the
nearest breakpoint rather than being discarded.

### Where the tune comes from

By default, the one stored inside the log — the copy that was actually running
when the log was recorded.

| Source | How |
| --- | --- |
| The log's own tune | Default for `.mlg` |
| A `.msq` file | **File ▸ Open tune…**, the **Open tune…** button, or drop a `.msq` on the window |
| Back to the log's own | **File ▸ Use the tune stored in the log** |

Which tune is in use is shown on the right of the toolbar — *from the log*, the
file name, or *none*. It turns amber when the opened tune does not match the log.

> **NOTICE:** Opening a tune by hand is necessary for a `.msl` or `.csv` log,
> which carries no tune at all. **For a `.mlg` it is usually the wrong thing to
> do.** VE calibration scales the numbers that produced the logged mixture; feed
> it a table you have edited since the drive and it will scale numbers the engine
> never ran. If the opened tune's fuel table differs from the log's, the sidebar
> says so.

### Two format quirks that are handled

Both would corrupt a table silently if they were not:

- Firmware pads an axis out to the table's width by repeating the top value,
  which would create zero-width bins. Consecutive duplicates are collapsed.
- The `…doz` axis variants are stored **rolled** rather than in order
  (`5200 5700 6100 6500 502 801 …`). Any axis that is not ascending is rejected
  outright rather than scrambling the rows.

`--tune` on the [dump tool](Development#the-dump-tool) lists the axes found
in a log.

## Data filters

### Why

A table built from a whole drive averages warmup, overrun and idle into the same
cells as the pulls you care about, and describes none of them.

### How

Each filter is a condition on a channel — `CLT ≥ 160`, `TPS > 1`,
`AFR between 9 and 20`. They combine with **AND**: a sample must satisfy every
ticked condition to be counted.

- Opening a log offers suggested filters for the channels *that log* has. They
  always arrive **switched off**, so opening a file never silently changes what
  a table counts.
- **+ Add filter** adds your own. Right-click one to delete it.

**Expected result:** the status line reports how many samples were excluded, so
a suspiciously sparse table explains itself rather than looking broken.

> **NOTICE:** **The axes re-scale to the samples that survive.** Filtering to
> warm running also tightens the RPM and MAP range onto that data, which is
> usually what you want — but it means two differently-filtered tables are not
> directly comparable cell for cell.

Filters are matched to channels by name and persist in
`%APPDATA%\OpenLogViewer\filters.json`. A filter naming a channel the log does
not have is reported and skipped, never applied as "reject everything".

## Tracing a cell back to the log

The table and the plot point at each other in both directions.

### Cell → log

**Click a cell.** The view switches to the plot, frames the samples that
produced that cell, and marks them.

An engine passes through the same RPM and load many times in a drive, so a cell
is almost never one stretch of the recording. Showing the span from a cell's
first sample to its last would cover most of the log. Instead the samples are
grouped into **visits**:

- The longest visit is framed and selected.
- Every other visit is shaded.
- The status line reports how many there were.

A cell averaged over twelve separate passes is a very different thing from one
sustained pull, and the table alone cannot show that.

### Log → cell

**Shift-drag a span on the plot, then switch to Histogram.** Every cell that
span passed through is outlined, with the count in the status line.

Those are the cells the pull is evidence about, and the ones worth editing on the
strength of it.

Samples a filter excluded are counted as *outside* rather than marked, since the
table does not rest on them. A span landing mostly outside says so rather than
quietly marking almost nothing.

The two compose: click a cell to see its longest visit framed on the plot, and
that visit is itself a marked span, so switching back rings the cell it came
from.

## Scatter

Switch to **Scatter** in the toolbar, or **View ▸ Scatter**. Same three channels
as the table, same filters, same panel — the samples left where they fell instead
of averaged into cells.

### Overplotting, and why the marks are computed

This is the one thing worth understanding about a scatter.

A drive is tens or hundreds of thousands of samples, and an engine spends most of
one in a small part of the map. Drawn one mark per sample they land on top of one
another thousands deep, and the colour that survives is whichever sample happened
to be drawn last — an accident of the order the log is in, presented with all the
authority of a measurement. Alpha blending only moves the problem: dense regions
saturate to a colour that is not any channel's value.

So the samples are aggregated onto the display's own grid before anything is
drawn — **3 × 3 pixels per block**, which is finer than any tuning table by two
orders of magnitude and still bounded by the size of the window rather than the
size of the log. Every mark is the mean of what actually landed under it.

**Hovering a mark gives the count behind it and the range its samples covered**,
because the spread is precisely what a mean hides.

### The colour scale is trimmed, not fitted to the extremes

A wideband touches both of its rails for a moment during a transient. Scaled over
the full range, those few blocks own the whole ramp while the drive around them
draws as one flat colour.

The ends are trimmed to the **2nd and 98th percentiles** of the occupied blocks.
Anything past them saturates — still drawn, still the most extreme colour on the
plot, still exact on hover, but no longer able to flatten everything else.

The legend marks a bound with `≤` or `≥` when the trim moved it far enough to
matter, so a number that is not the largest value on the plot is never read as
one. Where a channel has no outliers the trim is not taken and the legend says
nothing.

### Other scatter behaviour

- **Click a mark to trace it back to the log**, exactly as a table cell does:
  samples grouped into visits, longest framed, the rest shaded.
- **Colour by sample count** re-shades by how busy each block is, on a **log
  scale** — unlike the table's. A drive spends orders of magnitude longer at idle
  than anywhere else, and on a linear scale idle is the only block with any
  colour in it.
- Export offers the plotted points as CSV, one row per surviving sample carrying
  its index in the log, and the scatter as a PNG.

## Why the colours are what they are

Cells use a **single-hue sequential ramp**, light for high and dark for low —
not the green/yellow/red seen elsewhere.

A rainbow ramp has no inherent order: the eye cannot rank yellow against cyan,
and it collapses under colour-vision deficiency and in greyscale. Lightness ranks
unambiguously for everyone.

The ramp is validated against the application's own surface. The low end holds
2.71:1 contrast against it, so a barely-populated cell still reads as distinct
from an empty one, and cell text flips between light and dark ink to stay legible
on every step.

Where a table shows a **deviation** — with **Compare against** set — it uses a
two-hue diverging scale about a near-background neutral instead, because polarity
is not magnitude.

## Reference

| Item | Value |
| --- | ---: |
| Histogram columns | 2 to 40, default 16 |
| Histogram rows | 2 to 40, default 16 |
| Scatter block size | 3 × 3 device-independent pixels |
| Scatter colour trim | 2nd to 98th percentile of occupied blocks |
| Filter combination | AND across all ticked filters |
| Filter store | `%APPDATA%\OpenLogViewer\filters.json` |

## Related

- [VE calibration](VE-calibration) — turning a table like this into a
  suggested fuel table
- [User guide ▸ Export](User-guide#export)
- [Firmware definitions and channels](Firmware-definitions-and-channels)
