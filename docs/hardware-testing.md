# What still needs a controller plugged in

Everything here passes against fakes and is unproven against hardware. The list
exists because this project keeps finding that the two disagree — the scale
resolver was wrong on every Speeduino load axis and 2,391 tests were happy about
it, and the burn path reported success as failure on two firmware families until
a board was actually asked.

Ordered by what would do the most damage if it is wrong.

## The boards

| | |
|---|---|
| **Speeduino** | An Arduino Mega, currently enumerating as COM4 — the number drifts with whatever else has claimed a port since, so check `[System.IO.Ports.SerialPort]::GetPortNames()` rather than trust a number written down here. A bench board. Opening the port resets it, so anything unburned is undone by reconnecting — the safest thing to test on. Launch control is **enabled** at a 2,700 rpm soft limit, so its rev limits are not inert. |
| **rusEFI** | Currently COM5 (was COM8 — same caveat as the Speeduino row: check, don't trust the number). uaEFI board, bench, USB power. Reset with `cmd_reset_controller` = `Z\x00\xbb\x00\x00` framed and written straight at the transport. **Nothing else in `[ControllerCommands]` should be sent casually — it also holds `cmd_test_spk1..12`, which fire ignition coils.** Confirmed unreachable through `/tune/set` even with writes armed (item 12). |
| **MicroSquirt** | COM3, and it is **in a live car**. Read-only unless explicitly asked. |

Always: back the tune up first, check RPM before writing, and say what was
verified rather than what was attempted.

---

## 1. A restore that survives a power cycle

Restore and burn are each proven; they have never been run in one sequence.

- Read the tune, back it up
- Restore a `.msq` that genuinely differs
- **Burn it**
- Reset the board and confirm the restored tune is still there
- Restore the backup, burn, reset, confirm

Speeduino is the board for this. The reset it does on every port open makes the
verification free.

## 2. The derived-scale fix on the other two firmwares — rusEFI spot-checked

Fixed and proven on a Speeduino only. MS2Extra and MS3 also state scales as
expressions — `{0.01 * (maf_range + 1)}` on the MAF curve — and rusEFI does
too: `maxAcClt`, `boostCutPressure`, `minimumBoostClosedLoopMap` and others all
key their scale off `{useMetricOnInterface ? 1 : 1.8}`-shaped expressions.

- **Spot-checked, not exhaustive.** On a live rusEFI (`master.2026.09.03.
  super-uaefi.1822896871`, `useMetricOnInterface=1`): `maxAcClt=100`,
  `boostCutPressure=300`, `minimumBoostClosedLoopMap=0` — all read as sane
  metric values (°C, kPa) rather than the wrong-scale garbage the bug used to
  produce. Not the same as checking every `ScaleExpression` constant, and
  `useMetricOnInterface` was never toggled to see the other branch resolve.
- ~~Compare a TunerStudio-saved `.msq` for that firmware against the live
  tune~~ — not done; no TunerStudio install was available.
- MS2Extra and MS3 remain completely untouched.

Getting to this board at all needed the firmware-definition-fetch feature
(`GET /definitions/needed`, `POST /definitions/import`) working for real: this
rusEFI declared `master.2026.09.03.super-uaefi.1822896871`, nothing local
matched, `/definitions/needed` named the exact file and a source URL, and
importing the fetched file let the reconnect succeed — the first live proof
of that path end to end.

The bug this closes was invisible until a real TunerStudio file was compared
against a live controller. Our own round-trip tests all passed, because they
write files with our own writer at our own wrong scale.

## 3. The agent API's live stream — mostly done

The whole reason it was built for speed, and it had only ever seen a log.

- **Done.** Connected over `ws://…/live/stream?token=…` against the live
  Speeduino. Schema arrived first with real channel names (`SecL`, `RPM`,
  `MAP`, …), frames followed at a measured ~28 Hz average against a poll rate
  the app itself reports as 25 — pushed continuously, not gated on a window
  being open or focused.
- **Not reproduced.** Stalled a subscriber without reading for 15 s (~99 KB of
  frames at ~264 bytes each) and `skipped` stayed `0` the whole time, even
  though `/state`'s `samples` count kept climbing underneath — so the poll
  itself was not slowing down, which is the property that mattered, but
  nothing forced `_skipped` to actually increment. Reading `LiveSubscriber`:
  it only counts a frame as skipped if a *second* `Offer()` lands before the
  writer task has drained the first, and `SendAsync` only blocks once the
  OS's own socket send buffer is full — which 15 s of small JSON frames
  apparently never reached. Proving this bullet for real likely needs either
  a much longer stall (minutes) or the `raw=true` stream, whose frames are
  many times larger.
- **Done.** Sent `{"channels":["rpm","map"]}` mid-stream; a fresh
  `{"type":"schema","channels":["RPM","MAP"]}` came back immediately,
  followed by a two-value frame.

## 4. An agent writing to a real controller — done, on the Speeduino

`SetSetting` had only been driven against `FakeController`. Verified live: armed
writes, changed `displayB1`/`displayB2` through `/tune/set` and `/tune/apply`,
read each back off the ECU, then restored the originals. Disconnected and
confirmed `writesArmed` flipped to `false` and a write was refused ("writes are
not armed... it clears itself on disconnect") until re-armed. There is no burn
route on the agent API at all — `/tune/apply`, `/tune/set` and `/table/set` all
answer `burned:false`, and nothing in the route table can trigger one.

**Every writeable setting swept, not just a handful.** All 682 settings on the
bench Speeduino: read via `/tune/full` (which carries each one's declared
low/high or enum options), nudged to a different valid value, read back, then
reverted, at a pace kept under the rate limiter (§11). Result: 513 round-tripped
cleanly, 37 never got their nudge through (refused by the rate limiter even
after one retry — never took effect, no risk), and the final pass confirmed
**every setting was back at its original value**.

132 were skipped on purpose, and this is the interesting part: their live value
sits outside the range the firmware itself declares for them — `idleUpAdder`
reads `255` but declares `0-250`, `wmiRPM` reads `25500` against a declared
`0-10000`, and so on. These are firmware "disabled" sentinels (0xFF, 0xFFFF),
and the app's own out-of-range guard — there to stop a bad write, not cause one
— means an out-of-range original **cannot be written back through the agent
API once nudged away from it**. The first sweep attempt found this the hard
way: it nudged `idleUpAdder` to `250` (in range, accepted) and then the
"revert" to `255` was refused as out-of-range, leaving it stuck. Recovery was
a reconnect — the Speeduino resets on port open, which restored `255` from
EEPROM since nothing here is ever burned — not a call the agent API itself
offers. The second attempt added a pre-check skipping any setting already
outside its own declared range, and it is why the second sweep needed no
recovery at all.

**Fixed** in `TuneSettingsEdit.Set` (`src/OpenLogViewer.Core/TuneSettingsEdit.cs`):
the first value ever observed for a setting in an edit session is now frozen in
`_everObserved` and stays writable even once it is edited away from, regardless
of the firmware's declared range. The first attempt at this fix used the
existing `Original()` — reads live from the ECU's polled pages — and it looked
right against the unit test but still failed live: by the time the revert
call reached the board, the poll loop had already refreshed those same pages
to the nudged value, so "original" had silently become "whatever is there
now." Caught by re-running the exact live sequence that broke the first time
(nudge `idleUpAdder` to `250`, wait, revert to `255`) rather than trusting the
unit test alone — the test uses an offline tune whose pages never move, so it
could not have caught this. Verified live afterward: the revert now succeeds,
and a genuinely different out-of-range value (`999`, not the sentinel) is
still correctly refused.

`SetTableCell` (`/table/set`) is still untried on real hardware — nothing
above exercises the table-cell code path.

## 5. MicroSquirt burn

The third burn-command variant. Speeduino uses `b%2i`, rusEFI a bare `B`;
MicroSquirt is untested. Both the others answered `0x04` rather than `0x00`,
which is what the status-byte fix was for — worth confirming a third time.

**In a live car.** Engine stopped, and only with the owner's say-so.

## 6. Version capture on a real burn

Tested through `FakeController`. On hardware:

- Open a project, burn something, confirm a version is captured and marked burned
- Burn again with nothing changed and confirm it does not make a second version
- Record a log and confirm the sitting carries the version id

## 7. Wi-Fi OBD2 batching

Outstanding since the branch was called `wifi-obd2-and-batching`. Needs the Vgate
dongle and the car. See the detail in the older notes: the reconnect after a
batching death, the recovery proving itself on singles, and the false positive
where a key-off is written down as an adapter fault.

## 8. Live insights, and the smoothing mark

Small, but both were changed and neither has been seen working:

- Leave the Insights window open through a live session and confirm it re-measures
  every few seconds rather than every tick, and that the window stays responsive
- Turn smoothing on a channel and confirm the `∿` beside its name appears

## 9. The wire trace under a real, deliberately-interrupted link

`WireTrace` has only ever seen a `FakeController`'s scripted replies.

- Corrupt or interrupt a real session — unplug mid-poll, wiggle a connector to
  force a bad CRC — and confirm `GET /wire/events` reports the right
  `FailureKind` and timing for what actually happened
- Confirm `GET /wire/health` moves the way a person watching the link would
  expect: success rate drops, `sinceLastSuccessSeconds` climbs

## 10. Propose/apply against a real controller — partly done

`/tune/propose` and `/tune/apply` had only been driven against `FakeController`.

- **Done.** Proposed a two-setting change (`displayB1`, `displayB2`); the diff
  came back with real before/after values off the live ECU. Applied it, read
  the tune back, confirmed both landed, then applied the reverse to restore
  the originals.
- ~~Confirm the diff matches what TunerStudio itself reports for the same
  file~~ — not compared against TunerStudio; nothing above opened the same
  tune there.
- ~~Confirm the auto-captured `TuneVersion`'s note carries the rationale that
  was sent with the write~~ — not checked; no tuning project was open during
  these calls.

## 11. Guardrails on a live, running engine — two of three done

The RPM check, the rate limit, and the dangerous-constant confirmation were all
unit-tested against a scripted RPM channel, never a real one.

- ~~With the Speeduino bench board actually running (not idling), confirm
  `/tune/apply` and `/tune/set` refuse with "the engine is running above idle"~~
  — still open. The bench board was at RPM 0 (off) for everything below;
  nothing here has exercised the RPM check itself.
- **Done.** `hardRevLim` (matches the `RevLimiter` role) was refused without
  `confirmDangerous:true` — `"hardRevLim" matches the RevLimiter guard... Pass
  confirmDangerous:true if this change is intentional` — and went through once
  it was passed.
- **Done, with a wrinkle worth knowing.** Eleven `/tune/set` calls in one
  second: the first ten answered `409 "the write did not go through... Nothing
  has been changed"` because the value sent equalled the value already there,
  and the eleventh still hit the rate limit — `10 agent writes landed in the
  last 5 seconds`. So the limiter counts attempts, including ones the no-op
  guard itself refused, not just writes that actually changed something.

## 12. `[ControllerCommands]` stays unreachable — done, on the Speeduino

A negative test, best proven once against real hardware where getting it wrong
has a concrete consequence. Tried `/tune/set` with `name: "cmdtestinj1on"` —
Speeduino's `[ControllerCommands]` includes `cmdtestinj1on`/`cmdtestinj1off`,
which fire an injector directly. Refused as `"no such setting"` rather than
reaching the transport. Still worth doing on rusEFI too, since its
`cmd_test_spk1..12` fires ignition coils rather than an injector and nothing
above touched that board.

## 13. Staged files actually open where they're meant to

- Stage a tune (`/stage/tune`) and confirm the `.msq` opens cleanly in real
  TunerStudio
- Stage a table (`/stage/table`) and confirm the CSV imports correctly onto a
  real table via TunerStudio's own table-import

## 14. Raw, unfiltered live telemetry against real firmware — done, on the Speeduino

`includeRaw`/`?raw=true` had only ever decoded a firmware's declared block
shape against scripted bytes. Subscribed to `/live/stream` with `{"raw":true}`
against the live Speeduino: 181 channels, all sane — `map`/`baro` in kPa,
`coolantRaw`/`iatRaw` plausible, `rpm`/`afr`/`tps` matching what `/tune` and
`/state` already said, `loopsPerSecond`/`freeRAM` in ranges that make sense
for a Mega, no `NaN` or wild values anywhere in the set, including the
fields Speeduino's own `[Datalog]` leaves out of the named channel set
(`status1`/`status2`/`status3`, `testoutputs`, `errorNum`).

---

## Not hardware, still open

- **rusEFI `[PcVariables]` axes** — one table and two curves want axes that live
  in TunerStudio rather than on the controller, so they open blank
- Four low-severity review findings: `Fahrenheit` on a `"temp"` unit,
  `commandButton` drawn as inert text, `SettingsMenuEntry.Condition` parsed and
  never evaluated, and the Wi-Fi route disposing a source the view model holds
