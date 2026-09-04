# VE calibration

Suggests a new fuel table from logged mixture against the mixture the tune was
asking for.

- [What it does](#what-it-does)
- [Requirements](#requirements)
- [How to run it](#how-to-run-it)
- [Settings](#settings)
- [Wideband delay](#wideband-delay)
- [What it refuses to do](#what-it-refuses-to-do)
- [Reading the result](#reading-the-result)
- [Applying the result](#applying-the-result)
- [Troubleshooting](#troubleshooting)

---

## What it does

**VE** stands for *volumetric efficiency* â€” the number in a fuel table that
tells the ECU how much air the engine takes in at a given RPM and load. The ECU
meters fuel from that number.

The reasoning is one line:

> The engine took in a known amount of air. The ECU metered fuel for it using the
> VE number in the cell. The wideband oxygen sensor says what the mixture
> actually came out as.

Richer than target means the ECU thought there was more air than there was, so
the VE number is too high. Scale it by measured Ã· target.

The result is a **suggestion**. Nothing is written to the ECU, and nothing is
applied to the tune on screen.

## Requirements

| | |
| --- | --- |
| A log | With a wideband AFR or lambda channel, and the ECU's AFR target channel |
| A tune | Either embedded in a `.mlg`, or opened from a `.msq` |
| A tune table | The tune must carry a fuel table with axes, chosen under **Axis breakpoints** |

The **Suggest a new fuel table** checkbox is only enabled once the axis source
carries the tune's own numbers. It can only suggest a change to a value it can
read.

## How to run it

1. Switch to **Histogram**.
2. Under **Axis breakpoints**, pick one of the tune's own fuel tables.
3. Set **Compare against** to the AFR target channel.
4. Tick **Suggest a new fuel table**.

**Expected result:** the table shows how far each cell would move, as a
percentage, and a summary line reports how many cells were suggested, how many
were too thin, and the largest change.

Tick **Show the new numbers** to switch between the percentage move and the
suggested values themselves.

> **NOTICE:** The measured channel and the target channel must be the **same
> quantity**. Comparing an AFR reading against a lambda target, or a
> petrol-referenced AFR against an ethanol-referenced one, produces a correction
> that is wrong by the stoichiometric ratio and looks entirely plausible.

## Settings

| Setting | Default | Range | Units | What it does |
| --- | ---: | --- | --- | --- |
| **Min samples** | 12 | 1 and up | samples | A cell with fewer samples is left alone and counted as thin |
| **Max change %** | 15 | 0â€“100 | % | Largest move suggested for one pass. Larger corrections are clamped, not applied whole |
| **Wideband delay, s** | 0 (none) | 0 and up | s | How long the sensor takes to see the mixture metered now |

**Min samples** matters more than it looks. Two crossings on the way somewhere
else say more about the transient than about the fuelling there.

**Above that floor a cell is trusted in proportion to its evidence**, rather than
all at once. A cell with twelve samples and one with two hundred would otherwise
move identically the moment each cleared the threshold, though one is a
measurement and the other a glance. The correction is scaled by:

```text
n / (n + MinSamples)
```

â€” half at the threshold, approaching the whole of it as samples accumulate. A
thin cell stays near the number it already holds, which carries the weight of
however it was arrived at.

**Max change %** exists because fuelling is not the only thing that moves AFR. A
cell read during an accel-enrichment event or a gear change can imply a
correction far larger than the table is actually wrong by, and applying it whole
turns one bad reading into a hole in the table.

## Wideband delay

### Why it matters

**The wideband reads late.** The reading at a given moment is not evidence about
that moment: fuel metered on this revolution is burned, pushed out of the port,
carried down the pipe to wherever the sensor is, and only then measured â€” and the
sensor itself takes time to respond.

At steady state that costs nothing. Through a fast ramp the mixture from 3,000
rpm is credited to the 4,000 rpm cell, so the correction is not merely wrong in
size but lands in the wrong cell and smears across a region of the table.

### Setting it

**Wideband delay, s** takes the time in seconds and converts it to samples at the
log's own rate, so the same setting means the same thing on a 40 Hz tuning cable
and a 2 Hz OBD2 link. The panel reports how many samples it came to, since on a
slow log a request for 300 ms may round to nothing.

Only the measurement is shifted. The target is what the ECU was aiming for when
it metered the fuel, so it belongs to the same moment as the cell.

It defaults to **none**, because the right figure depends on where the sensor is
fitted and how long its pipe is. Typical values are 0.2 s to 0.4 s.

### Finding it from the log

**Press *Find it*.**

Samples landing in one cell were taken at about the same operating point, so the
mixtures they measured ought to agree with each other. Pair a cell with readings
taken too early or too late and readings from neighbouring conditions leak in,
and its samples start to disagree.

So the delay is swept, the disagreement within each cell measured, and the delay
where they agree best is the answer.

This is **signal alignment rather than tuning** â€” every cell is judged against
its own readings and never against a target, so a badly mistuned engine aligns
exactly as well as a well tuned one.

**Expected result: a band, not a point.**

```text
somewhere between none and 0.27 s, 0.20 s fits best
```

A sweep does not come to a point: several neighbouring candidates routinely sit
within their own sampling error, and naming the lowest of those would claim a
precision the log does not carry. What comes back is the range that cannot be
told apart from the best. Narrower with more data or a cleaner sensor, wider with
less.

### When *Find it* refuses

It reports the reason rather than returning a number it does not believe:

| Message | Meaning |
| --- | --- |
| The engine did not change enough | Held at one operating point, every delay pairs a cell with readings from the same conditions. They all score alike, and there is nothing in the log to find the answer with. This is the absence of evidence, not a small delay |
| Too few samples | Not enough data to distinguish candidates |
| Still improving at the edge of the search | Not a minimum but the end of where it looked |
| Open a log first | No log is loaded |
| Pick the axes and a target channel first | The comparison is not set up yet |

## What it refuses to do

The refusals are what make it usable:

- **A cell with fewer than Min samples is left alone** and counted as thin.
- **Cells the log never visited are untouched, not zeroed.**
- **A correction larger than Max change % is clamped,** not applied whole.
- **A zero or negative AFR target is not a target,** and those samples are
  skipped.

Use the [data filters](Histogram-and-scatter#data-filters) to exclude what you
do not want counted â€” up to temperature, engine running, off idle.

## Reading the result

The summary line reports three things:

1. How many cells were suggested a change.
2. How many were too thin to move.
3. The largest change.

A run where most cells are thin means either the drive did not exercise the map
or **Min samples** is set too high for the data you have.

## Applying the result

VE calibration **suggests** a table. It does not apply one.

Two ways to use the result:

| Route | How |
| --- | --- |
| Paste into a tuning application | **File â–¸ Export â–¸ Table as CSV**. The table is written in tuning-table shape â€” X across the top, Y down the side, highest row first |
| Edit the ECU directly | Switch to **Calibration** on a live connection and make the changes there â€” see [Editing a tune](Editing-a-tune) |

> **WARNING:** A suggested table is a suggestion. Review it before applying any
> of it. A cell suggesting a large change on thin data, or on a stretch of log
> where the engine was in transient, can move a table into detonation. Apply
> changes in steps and re-log between them.

## Troubleshooting

| Symptom | Likely cause | What to check |
| --- | --- | --- |
| **Suggest a new fuel table** is greyed out | The axis source is not the tune's own table | Pick a tune table under **Axis breakpoints** |
| Every cell reads "thin" | Not enough data per cell | Lower **Min samples**, use fewer bins, or log a longer drive |
| Corrections are large and inconsistent | Warmup, overrun or transients are being counted | Add filters: coolant up to temperature, throttle off idle |
| Corrections all sit at the clamp | The measured and target channels are different quantities | Confirm both are AFR, or both lambda, and referenced to the same fuel |
| Corrections smear across a region | Wideband delay not set | Press **Find it** |
| **Find it** says the engine did not change enough | Steady-state log | Log a drive with ramps and changes of load |
| The table looks nothing like the tune | The opened tune is not the one the log ran | **File â–¸ Use the tune stored in the log** |

## Related

- [Histogram and scatter](Histogram-and-scatter)
- [Editing a tune](Editing-a-tune)
- [Firmware definitions and channels](Firmware-definitions-and-channels)
