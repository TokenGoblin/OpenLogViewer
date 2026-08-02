# Changelog

## Unreleased

Everything below landed in one run of work. Grouped by what it does for you
rather than by commit.

### Added

**Live connection to a MegaSquirt.** *Connect ▾* lists the serial ports; picking
one reads the ECU's signature, matches an INI to it, and starts recording. A
live session is an ordinary log, so the sidebar, filters, calculated channels,
the heat table and VE Calibration all work on it as they do on a file — and
channels take the names recorded logs use, so presets and filters transfer.

The INI is matched to the signature the ECU reports and a session is refused
when none matches. Firmware versions move channels inside the realtime block, so
the wrong INI does not fail — it reads every channel from the wrong offset and
returns numbers that look reasonable.

Recording is continuous rather than saved at the end, and losing the link does
not end the session: key off and key on is normal, so a lost link is waited on
and the session carries on into the same recording when the ECU returns.

Read-only throughout. The only commands sent ask what the firmware is and read
the realtime page; nothing can write a value, burn a page, or change a setting.

Verified against a MegaSquirt 3 on a bench: 249 channels at about 16 Hz with no
retries, surviving repeated unplugs.

**VE Calibration.** Suggests a new fuel table from logged AFR against the AFR the
tune was asking for. In histogram view, pick one of the tune's own tables under
*Axis breakpoints*, set *Compare against* to the AFR target, and tick **Suggest
a new fuel table**.

It refuses to guess. A cell under the sample threshold is left alone and
reported as thin; a correction beyond the per-pass limit is clamped rather than
applied whole; a cell the log never visited stays untouched rather than zeroed;
a zero or negative target is not treated as a target. Toggle between how far
each cell moves and the new numbers themselves, and export either as CSV in the
shape a tuning table has.

Verified against a 60,356-sample MS3 log and its tune — 128 of 256 cells
suggested, 62 too thin — with the same result whether the tune is read from
inside the log or from the `.msq` alongside it.

**Opening a tune.** Tables come from the log by default. *Open tune…* sits
beside *Open log…* in the toolbar and again next to *Axis breakpoints*, and a
`.msq` can be dropped on the window — needed for `.msl` and `.csv` logs, which
carry no tune. The toolbar always names the tune in use, so which one is loaded
is visible from the main view rather than only from the histogram sidebar.

For an MLG it is usually the wrong thing to do, so the sidebar compares the two
and warns when the opened tune's fuel table differs from the log's: VE Calibration
scales the numbers that produced the logged AFR, and a table edited since the
drive scales numbers the engine never ran.

**Calculated channels.** Define a channel from the ones the log already has:
`AFR - AFR Target 1`, `RPM * Torque / 5252`, `if(Boost psi > 0, Boost psi, 0)`.
Once built they are ordinary channels — plottable, usable as a histogram axis,
available to filters, included in exports — and marked **ƒ** in the list.

Names need no quoting even with spaces in them. Operators `+ - * / % ^`,
comparisons, `&& || !`, and `abs sqrt min max clamp floor ceil round log log10
exp pow sign if`. Missing readings propagate, including through comparisons.
Stored in `math.json` by name and expression, so a set written for one car
applies to any log carrying those channels.

**Export.** *Export ▾* in the toolbar. In log view: the plotted channels or all
of them as CSV, and the plot as a PNG. In histogram view: the binned values, the
per-cell sample counts, and the table as a PNG. Mark a span first and the CSV
covers only that span.

Numbers are invariant-culture and each value is the shortest text that reads
back as the same sample. An exported log opens again in OpenLogViewer, gaps in
logging included. `--export <folder>` does the same without dialogs.

**Fourteen colour schemes.** Two dark, two light, eight after well-known editor
schemes, and a high-contrast pair. Chosen from the toolbar and remembered.
Chrome, plot, heat table and scrollbars all follow the scheme.

Each scheme carries its own trace palette, and switching re-picks the colours of
everything plotted. Those palettes are not the upstream editor colours: a syntax
theme colours short spans that are never adjacent, and every canonical palette
failed when measured for two traces crossing mid-plot. Each keeps its scheme's
hues and relative saturation, moves only in lightness, and was checked for
lightness range, chroma, contrast against its own background, and neighbour
separation under protanopia and deuteranopia.

`--theme <id>` starts in a scheme for one run without changing the saved
preference.

### Changed

**Logs use about half the memory.** Channel samples are stored as 32-bit floats.
No logger produces more precision than that — the widest MLG field is an f32 and
text logs come from short decimal strings. A 179-channel, 12.4 MB log now
retains 26.6 MB instead of 52 MB.

The time base is the exception and keeps full precision, because it accumulates:
ten hours into a 413 Hz recording the float step exceeds the interval between
samples, and time would stop advancing.

**Logs load with about half the allocation.** Both readers decode straight into
float columns instead of building the whole log as doubles first. The delimited
reader also stopped growing a list per column and copying it out. For the
reference log, 90.3 MB allocated became 39.6 MB and peak working set 113 MB
became 61 MB.

### Fixed

- **MLG scales carried float error into every sample.** Descriptors store the
  scale as a 32-bit float, so a channel scaled by 0.1 held 0.100000001490116.
  A raw 341 decoded to 34.10000228 instead of 34.1 — one rounding step off,
  hidden behind the display format but plainly wrong in an exported file.
- **The sidebar listed "Time" as a plottable channel.** The time base is built
  from its own copy of the column, so it is never the same object as the entry
  in the channel list, and both places that excluded it compared references —
  and therefore excluded nothing.
- **PNG exports were shifted and clipped.** `RenderTargetBitmap.Render` draws a
  visual at its position within its parent, so the plot came out offset by the
  width of the sidebar with its right edge cut off.
- **Scripted runs could hang silently.** `Dispatcher.InvokeAsync` captures an
  exception into the operation it returns rather than raising it, and nothing
  awaited that operation, so a failed `--screenshot` or `--export` left the app
  open with no error and no exit. Both now report and exit non-zero.
- **Both diverging heat-table arms faded towards the same near-white**, so the
  worst rich cell and the worst lean cell looked alike.
- **An accent close in lightness to both the background and the text** left a
  hovered button's glyph unreadable.
- **A blue heat-table ramp starting too dark** sat level with an empty cell,
  making "never visited" look like "visited once".
- **App tests read and wrote the user's real settings.** The harness built a
  temporary directory and then constructed the view model with default stores.

### Internal

- Trace colours come from the view model's own theme rather than global state,
  which parallel test classes could race on.
- `MathChannel` deliberately avoids `required` members: a required member
  missing from the JSON fails the whole document, so one hand-edited entry with
  a typo would take every other definition with it. `LogFilter` has the same
  shape and the same latent behaviour, left as shipped.
- Heat-table ramps are derived in OKLab from each theme's anchors and pinned by
  tests, rather than listed per theme.

### Still open

- Loading blocks the UI thread. A 12.4 MB log freezes the window for about
  200 ms; a much larger SD-card log would be seconds.
- `MainViewModel` has grown past 1,000 lines and holds theme, filters,
  histogram, presets, calculated channels and the channel list.
- Text logs allocate about 59 MB parsing a 3 MB `.msl`, nearly all of it
  per-cell substrings. Retained memory is unaffected; this is load-time churn.
