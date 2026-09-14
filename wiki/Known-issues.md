# Known issues

Defects that have been **verified against the source** and not yet fixed, plus
the things that are not covered by any test. Kept here rather than in an issue
tracker so that it travels with the code and is read by whoever is working on it.

A finding earns a place here by being reproduced, not by being suspected. Each
says what actually goes wrong, because "review comment 7" is unusable six months
later.

Last reviewed against the source: **2026-09-13**.

## Open defects

### A MaxxECU over USB still has a channel list that depends on the drive

Largely fixed on 2026-09-13; what remains is written down here because the shape
of it will surprise somebody.

Over USB a MaxxECU names its own channels and sends one **only when its value
changes**, so a list learnt by listening is a list of whatever was moving.
Measured on the bench Race: 136 channels on the first connect of a day, 49–55 on
every connect after. Listening longer does not help — a raw listen had 51 ids
after a second and 55 after sixty. The large first number is a queued backlog
being drained, not a full dump.

With the engine off that left out engine speed, coolant, lambda and ignition
angle — and a log's columns cannot change once it has rows, so connecting before
turning the key lost them for the whole session.
`MaxxUsbSource.AlwaysLogged` now makes the fifteen channels worth having into
columns whatever the ECU has said: the fourteen MaxxECU subscribes to for its own
Bluetooth dash, plus throttle position.

What is left:

- A core channel the ECU has not yet reported **reads zero** until it does. Right
  for engine speed with the key off, wrong for a coolant temperature, and it
  lasts until the value first moves.
- Everything outside the core set is still whatever happened to be moving, so two
  logs of the same car can have different columns.

### A burn interrupted by unplugging can lose its own message

`EcuConnection.cs`, in `BurnPage`. The `finally` puts `WriteTimeout` back without
checking the port is still open. `SerialEcuTransport` guards the port being null
and not it being closed, so on a controller unplugged mid-burn the setter can
throw over the carefully worded exception explaining that the burn **may still
have completed** — which is the one thing somebody needs to read before deciding
whether to power-cycle a car.

Lower confidence than the rest: it needs the unplug to reproduce and has not been
seen. See [verify it on hardware](#what-has-never-been-tested-on-hardware).

## Performance and structure

Measured on 2026-09-10 against a 20.9 MB log — 60,356 samples across 179
channels — rather than estimated. None of these is a defect; each is a thing that
will bite at a size nobody has reached yet.

- **Loading blocks the UI thread.** That log decodes in 233 ms and the window is
  frozen for all of it, with no progress shown. A large SD-card log would be
  seconds.
- **`MainViewModel` is 6,020 lines**, with 288 public members and 110 fields
  across three partial files. It holds theme, filters, histogram, presets,
  calculated channels, the channel list, the tune, the live session and the dyno.
  This is the structural reason behind the recurring mistake of wiring new state
  into one path and not its siblings: nobody can hold 288 members in their head.
- **The plot rebuilds every trace on every mouse move.** `LogPlot.OnRender`
  builds each visible channel's geometry from the samples and `OnMouseMove`
  invalidates, so there is no cache between them. At the sizes measured that is a
  few milliseconds and invisible; on a much larger log it is the first thing that
  will feel heavy under the cursor. The fix is to cache the geometry against view
  range and lane rect, and to draw the cursor, hover card and selection into a
  separate visual so pointer movement never touches the traces.
- **Text logs allocate about 59 MB parsing a 3 MB `.msl`**, nearly all of it
  per-cell substrings. Retained memory is unaffected; this is load-time churn.
  Carried forward from an earlier measurement and **not re-checked**, for want of
  an `.msl` to measure.

## What has no test at all

### The seven drawn views

`LogPlot` (1,009 lines), `ScatterView` (513), `CurveView` (498),
`HistogramView` (441), `TuneTableView` (396), `GaugeView` (383) and
`DynoView` (326). Nothing in the suite constructs one, so about 3,600 lines of
the most interaction-heavy code in the application is unexercised.

An attempt at construct-and-render tests was made and withdrawn — it passed and
failed the same path between runs, because a WPF element's layout belongs to the
dispatcher it was created on and the harness did not get that right. The
infrastructure needs doing properly before the tests are worth having.

Synthetic mouse input is **not** the way in. It has been tried on this project
and the clicks land in whatever window has focus, which is somebody else's. The
screenshot flags (`--power`, `--dyno`, `--screenshot`) are how a view gets driven
from outside.

### A corpus of real files

Every serious defect in this project has been found by pointing it at real logs,
tunes and definitions rather than at fixtures. The time-base defect fixed on
2026-09-10 was silently wrong on **three of seven** real recordings and invisible
to 2,000 passing tests.

There is no committed corpus, and the files are not ours to redistribute. What
would help is a checked-in manifest of expectations — sample count, duration,
real-or-counted time base, channel count — against files a developer points the
suite at locally.

## What has never been tested on hardware

The Wi-Fi OBD2 path has never met the real Vgate dongle. Everything deciding the
batching verdict was tested against a fake modelled on a second-hand description
of it.

1. The whole path once, end to end.
2. Reconnect after a batching death — `Elm327Source.Recover()` reopens with no
   pause, and these adapters take one client at a time.
3. That recovery comes back on single requests, which is the entire premise of
   `Condemn()`.
4. The false positive: key off mid-drive, key on, recover, then confirm
   `Obd2BatchDeaths` in `settings.json` is still empty.
5. Poll rate with batching live, and what the single-read fallback costs.
6. Whether partial batched replies happen at all — that fallback was written from
   reasoning about segmented frames, not from an observed reply.

Elsewhere: read, write and burn are proven on a Speeduino and a rusEFI uaEFI, and
read is proven on a MicroSquirt. **The MicroSquirt write path has never been
exercised** — it is in a car — and should be run on a bench board first.

A MaxxECU over **USB** is proven on the bench Race, on 2026-09-13, from the
application and not only from the library: connect, learn, read, record to a
file and read that file back, plus recovery, contention with another program,
the wrong baud rate being refused, a thirty-second soak at 36.5 rounds a second
with no retry, and the two writes of MTune's connect handshake (`0x3B` then
`0x07`), which the ECU acknowledged exactly as it acknowledges MTune. What those
two writes are *for* is still unknown — they change nothing observable in the
telemetry — and no other write has ever been sent.

A MaxxECU over **Bluetooth** has not been reached since the connect-time proving
read was added. No MaxxECU Bluetooth module is paired with this machine, so it
could not be tried.
