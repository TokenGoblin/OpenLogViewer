# Known issues

Defects that have been **verified against the source** and not yet fixed, plus
the things that are not covered by any test. Kept here rather than in an issue
tracker so that it travels with the code and is read by whoever is working on it.

A finding earns a place here by being reproduced, not by being suspected. Each
says what actually goes wrong, because "review comment 7" is unusable six months
later.

Last reviewed against the source: **2026-09-11**.

## Open defects

### A burn interrupted by unplugging can lose its own message

`EcuConnection.cs`, in `BurnPage`. The `finally` puts `WriteTimeout` back without
checking the port is still open. `SerialEcuTransport` guards the port being null
and not it being closed, so on a controller unplugged mid-burn the setter can
throw over the carefully worded exception explaining that the burn **may still
have completed** — which is the one thing somebody needs to read before deciding
whether to power-cycle a car.

Lower confidence than the rest: it needs the unplug to reproduce and has not been
seen. See [verify it on hardware](#what-has-never-been-tested-on-hardware).

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
