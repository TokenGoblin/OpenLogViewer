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

**Any OBD2 car, through an ELM327 dongle.** The one connection here that needs
nothing known in advance: the parameter numbering, the scaling and the units are
the same on every OBD2 vehicle by law, and the car itself reports which
parameters it answers to — so a connection produces named, scaled channels on a
car nobody has ever plugged this into. What it costs is speed. OBD2 has no
realtime block, so each parameter is its own request and a row of readings takes
a good part of a second. Fine for watching a car; no use for catching a misfire.

Both radios, because these dongles split between them and the difference is
invisible until it fails. A Bluetooth Classic adapter pairs as a serial port and
appears as a COM port; a Bluetooth LE one never becomes a COM port however long
you wait, and is reached over GATT instead. Adapters are recognised by the name
they advertise, which is the only clue either radio offers — and the list knows
that an OBDLink advertises as "ScanTool.net-…", after the company rather than
the product, because a dongle that goes unrecognised gets probed as a MegaSquirt
and reported as an unknown ECU.

Adapters are named by what they are rather than by what they claim. All of them
answer `ATI` with an ELM327 version whether or not that is true; the STN chips
inside an OBDLink answer `STDI` and `STI` as well, so those report as "OBDLink
r2.6 (STN1100 v2.2.2)" and a clone that refuses both still reports its ELM327
version as before.

Verified on a live vehicle with two dongles: a BLE `OBDII` clone, and an OBDLink
r2.6 over Bluetooth Classic — 24 channels, connected in eight seconds, the car
settling on ISO 15765-4 with two modules answering.

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

**Four more calculators, and one that ties them together.** *Engine recipe* takes
a displacement, a power target and the two engine speeds and hands back a parts
list: the air it takes, the boost that needs, a turbocharger, injectors and a
pump. Every part of it is sized on one mixture and one fuel consumption, so the
pieces match each other — a turbo sized at one assumption and injectors at
another gives a car short of one of them and neither calculation would say
which. The margins stay separate and named: duty on the injectors, headroom on
the pump, flow to spare on the compressor.

The peak torque speed is an input because it decides as much as the power figure
does. Airflow at the power peak says whether a compressor is large enough;
airflow at the torque peak says whether it is too large — one with plenty left at
7,000 rpm can be sitting off the left of its map at 3,500 and never spool.

*Turbo sizing* is the same sum on its own, using the turbocharger maker's own
equations so it can be checked against the tool everyone already uses: their
published worked example comes out at their 57.3 lb/min and their pressure ratio
of 2.0. The catalogue carries inducer size and the maker's horsepower rating —
which on the G series is the model number — and derives flow from that rather
than transcribing it off a compressor map, since the same map gives a different
maximum depending which island you read it at.

*Drag strip* gives quarter and eighth mile from power and weight, and reads a
timeslip the other way. Trap speed and elapsed time are not equally trustworthy:
by the far end a car has had a quarter of a mile to forget a bad launch, so its
speed is very nearly a measure of power against weight, where the time never
forgets. So the trap is read for power and the time for the start line — a slip
that trapped like 400 hp and ran half a second slower than that trap deserved
lost the time in the first sixty feet, and nothing done to the engine will show
up until it is found.

Volumetric efficiency is chosen as a kind of engine rather than typed as a
number, since that is what somebody planning a build actually knows: an older
two-valve breathes 75 per cent where a race engine on tuned inlet lengths passes
100. Charge temperature takes Celsius or Fahrenheit, fuel selection moves the
mixture and the fuel consumption together — moving one without the other asks
for half again the air on E85 and buys two sizes too much turbocharger — and the
calculators are grouped down the side rather than tabbed, with the whole set one
click away on the toolbar.

**Editing a tune table by hand.** Nudge, scale, set and interpolate from buttons
rather than only from keys, and a readout above the table saying what the
selected cell would become — *2600 rpm × 100  98 → 100  +2 % (2.0% more)* —
before any of it is sent. Interpolation fills the middle of a selection from its
ends, in a row, a column or a rectangle, and leaves the ends exactly as they
were.

**Estimated horsepower, two ways.** *Tools ▸ Estimate power…* adds calculated
channels that work out what the engine was making, from a log a logger already
recorded.

Speed density takes the air from the manifold — pressure, temperature, engine
speed and how completely each stroke fills — and turns it into power through the
mixture and the fuel consumption. The injector route ignores the air entirely and
counts the fuel instead, from how long the injectors were held open and how hard
the rail was pushing on them. Dead time comes off the pulse width first, batch
injection is worth exactly twice sequential, and flow follows the square root of
the pressure *across* the injector — so a rail measured against the atmosphere
while the injector sprays into a boosted manifold flows less than its rating, by
the boost. Where the car has a mass air flow sensor, that is a third route.

The two rest on almost nothing in common: one leans on volumetric efficiency, the
other on injector data. So a **spread** channel reports how far apart they are,
and that is the output worth watching. A few per cent is noise. A steady gap
means one of the two is wrong — and the assumed fuel consumption cancels out of
the comparison, so the spread is telling you about the engine rather than about
the guess. On a test log with the injectors described correctly the two agree to
0.0%; describe the injectors as 550 cc when they are 1,000 and it reads −50%;
claim 120% volumetric efficiency on an engine making 95 and it reads −21%.

Everything the log can answer, it is asked: the mixture, the volumetric
efficiency, the rail pressure and the manifold are read from the log wherever
they are there, whatever the firmware called them and whatever units it chose —
kPa or psi, °C or °F, lambda or an air-fuel ratio. The dialog only collects what
a log cannot say. Methods the log cannot feed are not offered, and it says which
sensor each one wanted.

None of it is a dyno, and it says so. Every method multiplies by a brake specific
fuel consumption that has been assumed rather than measured, so the absolute
figure carries that error. What they are good for is shape and change — where the
power is made, what a modification did, whether two runs match — and those are
ratios, which the assumption divides out of.

**Calculated channels.** Define a channel from the ones the log already has:
`AFR - AFR Target 1`, `RPM * Torque / 5252`, `if(Boost psi > 0, Boost psi, 0)`.
Once built they are ordinary channels — plottable, usable as a histogram axis,
available to filters, included in exports — and marked **ƒ** in the list.

Names need no quoting even with spaces in them. Operators `+ - * / % ^`,
comparisons, `&& || !`, and `abs sqrt min max clamp floor ceil round log log10
exp pow sign if`. Missing readings propagate, including through comparisons.
Stored in `math.json` by name and expression, so a set written for one car
applies to any log carrying those channels.

**Calculators.** *Tools ▸ Calculators*: nine of them, grouped down the side into
air and boost, fuel, engine and drivetrain, and all recomputing as you type. A
list rather than a row of tabs because tabs run out of width at about eight, and
there is no reason to expect this to stop at nine.

Engine takes a bore, a stroke and a cylinder count and answers three questions at
once, because all three rest on the same two numbers: how big the engine is, how
fast the pistons are going, and how hard it squeezes. Mean piston speed says more
about what an engine is being asked to survive than its rpm does — seven thousand
is an easy afternoon for a short stroke and the end of the road for a long one.
Compression is the static ratio from the chamber, gasket, deck and crown, with
the sign conventions spelled out, and alongside it an index of what your boost
does to it: less compression buys boost, and it says roughly how much.

Boost converts psi, bar and kPa, and gauge against absolute — an ECU reports
manifold pressure absolutely, so ten psi of boost is 170 kPa and not 69.

Pressure ratio is the compressor's own, taken at the compressor rather than at
the manifold: the air filter costs something on the way in and the intercooler
costs something on the way out, and both push the ratio up. Altitude is an input
and defaults to sea level. It matters more than people expect — a gauge reads
against whatever the engine is breathing, so the same twelve psi is a ratio of
2.10 at sea level and 2.34 at five thousand feet, which can be the difference
between a compressor sitting in the middle of its map and one against the edge
of it. Set the altitude or type your ECU's own barometric reading, and every
other tab follows it.

Injectors size from power, cylinders, BSFC and duty, with the cc conversion
taken from the fuel's own density. Each fuel is advised with the BSFC it
actually wants, worked out from its energy content: E85 needs about half again
petrol's and methanol a little over twice, and leaving it at petrol's is what
undersizes an injector.

The fuel pump gives what is burned and what to look for with headroom over it,
and then names pumps that would do it — Walbro and AEM part numbers, with how
many of each. Compared at the pressure the pump will actually see rather than the
one its headline figure was measured at, because a rail at 43 psi with 20 psi of
boost on it is 63, and every pump makes appreciably less there than the number on
its box. Alcohol fuels are only ever offered pumps rated for them. Past three in
parallel it says so and points at a mechanical or brushless pump instead of a
fourth. The BSFC follows the fuel, scaled by energy content so a figure you
measured or chose for an aspirated engine survives the change, and a legend
alongside shows the typical figure for every fuel.

Gearing gives road speed against engine speed in every gear: what each is worth
at the redline in mph and km/h, how long-legged it is per thousand rpm, where the
engine lands on taking the next one, and what it turns at a cruise. Tyres are
entered as written on the sidewall. The squat of a loaded tyre is an input rather
than a silent error — a rolling tyre covers about three per cent less ground than
pi times its diameter, so working from the geometry alone reads that much fast,
which is roughly what a factory speedometer already does and why the two are easy
to confuse. Top speed is named as the geared one: the tallest gear at the redline
and nothing else, which is a ceiling rather than a speed the car will see.

Octane estimates what blending ethanol, methanol or E85 into pump petrol is
worth, as a chart of the six pump grades against blend fraction, and shows how
much colder the blend runs the charge. The first splash of ethanol being worth
far more octane than the last is not the mystery it looks like: octane blends
very nearly linearly by molecule, and ethanol's molar mass is 46 against petrol's
105, so a tenth of the volume is a fifth of the molecules. Estimated on that
basis rather than by volume, and checked against measured blends — on an 88 RON
blendstock it gives 92.4 at E10 and 98.7 at E30 where the measurements are 92.4
and 98.6.

Lambda converts either way on ten fuels, with the blends computed from their
constituents by mass. Airflow gives demand in cfm, in the pounds per minute a
compressor map is read in, and in cubic metres an hour — and what that air is
worth in power on petrol, ethanol and methanol. The answer there is worth
knowing: it is very nearly the same on all three, because a pound of air carries
about as much energy whichever fuel arrives with it. What makes the alcohols
worth having is knock resistance and charge cooling buying boost and timing,
which arrives as more air rather than as more power per pound of it.

Every figure is computed in the core and tested there against numbers a tuner
would recognise — a turbocharger maker's own worked example, the published
standard-atmosphere table, familiar injector sizings — rather than against the
formulas restated. The advice the window gives is tested alongside them, because
a tuner acts on a sentence as directly as on a number.

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
