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
| **MaxxECU Race** | USB only, and never a COM port — see item 15. FTDI `VID_0403&PID_9728`, serial `MX000000`, D2XX driver. Bench, 12 V applied, engine not running. |

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

`SetTableCell` (`/table/set`) — **tried, found a real bug, fixed.** The very
first live call (`VE Table`, cell `[0,0]`, `33 → 34`) answered `500 "the
application failed to answer"` — `"This type of CollectionView does not
support changes to its SourceCollection from a thread different from the
Dispatcher thread."` The read-back showed `34`: **the write had actually
landed on the ECU**; the 500 was `MainViewModel.WriteTableToEcu` refreshing
`SelectedEcuTable` (a DataGrid's bound `CollectionView`) *after* the real
write succeeded, from the HTTP listener's own thread rather than the UI
thread — a false failure that would have told an agent a write didn't happen
when it did. This is a case the unit tests could not have caught: everything
runs synchronously on one thread there, so there is no "wrong thread" for
WPF to object to. Fixed by marshalling that one refresh through the
`OnUiThread` helper this codebase already uses for the agent activity
indicator — a human clicking Send still updates the screen inline on the
same call it always has; an agent's write now updates it asynchronously
instead of throwing. Reverified live: `200`, correct value both ways,
reverted cleanly.

**A second, real bug, also found and fixed: table cells silently clamp, and
the API used to lie about it.** `TuneEdit.Hold` deliberately clamps a cell
into the firmware's declared range rather than refusing it out of range (see
the comment at `TuneEdit.cs:307` — right for a person scaling a whole table
by a percentage, wrong for a single agent write). `/table/set` was echoing
the *requested* value back as `written`, not what actually landed. Found by
writing three more cells beyond the first: `Ignition Advance Table` landed
correctly, but `Dwell map` (`25.5 → 24.5` requested) and `Fuel trim Table 1`
(`127 → 128` requested) read back as `8` and `50` — both tables' axis bins
are degenerate (`"xBins":[10200,10200,10200,10200]`, all-identical), a sign
these features are simply unconfigured on this tune, and their cells' true
declared range sits below the `25.5`/`127` sentinel already resident there.
`/table/set` now returns `value` (what actually landed, read straight from
the edit that was encoded and sent), `requested`, and `clamped`.

A first version of that fix re-read the on-screen table via the same bridge
`GET /table` uses, and looked right in the unit test — then reported a
**stale** value live: `SelectedEcuTable`'s refresh (the fix two paragraphs up)
is dispatched to the UI thread asynchronously, so reading it back
synchronously, right after the write returns, can catch it before that
refresh has actually run. Fixed properly by reading `edit.Values[column,row]`
directly — the working copy `Hold` already clamped and `WriteTunePage` already
sent, unaffected by any UI dispatch. `IAgentBridge.SetTableCell` now returns
`AgentCellWrite(Refusal, Value)` instead of a bare `AgentRefusal?`, so the
value is available at the point it is known rather than reconstructed by
re-reading something else afterward. Verified live, twice: the first version
of the fix confirmed against `Dwell map` looked right (`value: 25.5`) but a
fresh `GET /table` immediately after showed `8` — caught by not trusting the
first green result. The corrected version's reported value and an immediate
fresh read now agree.

**Not fixed: a table cell's own out-of-range sentinel can't be written back
either**, the same class of problem item 4's settings fix already covers, and
found the same way — writing `Dwell map` back toward `25.5` clamps to `8`
every time, with no way to the original short of a hardware reset. The
settings fix worked by remembering the first value `TuneSettingsEdit` ever
saw for a key, for the life of that one edit session. Table cells don't have
an equivalent session: `SelectedEcuTable`'s setter builds a brand-new
`TuneEdit` on every single `/table/set` call (`MainViewModel.cs:2528`), so
there is no persistent `_original` to anchor a "first observed" exception
against — it would need a cache one level up, keyed per table and cell, kept
across calls the way `_settingsEdit` already is for settings. Left alone
rather than rushed: two real bugs fixed and reverified live in one session is
enough surface area for one sitting, and this one is narrower in practice —
most cells in an active tune are configured and in range; it is the
unconfigured, sentinel-holding ones this bites.

**Added: `GET /limits`.** The sweep's 37 rate-limit refusals were all learned
the hard way, one 409 at a time, with no way to ask the API what the shape of
the limit actually was short of counting failures. `GET /limits` (and a
`limits` field on `GET /`, so it is the first thing an agent sees on connect
rather than something it has to think to ask for) now states the write rate
(10 per 5 s), the per-write magnitude cap (half a setting's declared range),
that nothing here ever burns, that arming clears on disconnect, and that a
`DangerousConstants` match needs `confirmDangerous:true`. Verified live against
the Speeduino: `{"writeRateCount":10,"writeRateWindowSeconds":5,
"maxChangeFractionOfRange":0.5,"burns":false,"writesArmedClearsOnDisconnect":
true,"dangerousSettingsNeedConfirmation":true}`. Not re-verified against
rusEFI, but the route is board-agnostic — it states constants, not anything
read off a controller.

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

## 15. MaxxECU over USB — done, on a real Race, with a driver dead end resolved

**A MaxxECU's USB is never a COM port.** Confirmed the hard way before any protocol
work: `SerialEcuTransport` needs one and MaxxECU's own driver (Maxxtuning-signed
`ftdibus.inf`, labelled `"MaxxECU USB - Bus/D2XX Driver"`) never creates one — no
`FtdiPort` section, no `ftser2k`, `FT_GetComPortNumber` answers −1. Verified by
installing MTune, installing its bundled FTDI driver package via `DPInst_x64.exe`,
uninstalling and replugging the device to force a clean re-enumeration, and
watching Windows bind it to the same bus-only driver every time. This is vendor
intent, not a misconfiguration — MTune talks to the same chip through FTDI's
D2XX library directly, confirmed by watching it hold the device open.

**Blind protocol guessing failed; recovered protocol documents succeeded.**
`MaxxProtocol.Activation`/`Subscription` are reverse-engineered from *Bluetooth*
captures and share no byte of framing with USB — a 13-point baud-rate sweep
(9,600 to 3,000,000) against them produced only small, shifting garbage,
never a clean reply. What actually worked: three recovered documents
(`MAXXECU_USB_PROTOCOL_RECOVERED.md` and others, supplied mid-session) from an
independent reverse-engineering effort that had already captured a real MTune
session and decompiled the firmware dispatcher. **921,600 baud** — not any of
the thirteen tried — with its own request/reply framing, its own CRC-32/MPEG-2
checksum, and a self-describing telemetry stream that names every channel it
carries rather than needing a subscription agreed in advance.

**The working implementation came from `origin/virtual-dyno`**, an
independent, unrelated-history line of this same project (no common ancestor
with `main` — confirmed via `git merge-base`) with roughly 250 commits
including a complete, tested `FtdiEcuTransport` / `MaxxUsbProtocol` /
`MaxxUsbSource` built against exactly this protocol. Rather than merge two
unrelated histories, the relevant files were pulled directly
(`git checkout origin/virtual-dyno -- <paths>`): the transport, the USB
protocol and source, the CAN sniffer they share code with (`MaxxCan`,
`MaxxCanSource`, `CanFrame`), the tune reader/writer (`MaxxTune`,
`MaxxTuneDefinitions`), and their existing tests — 2,090 tests, all green
after two small integration fixes (a missing `CanFrame.cs` dependency, and the
test fixture glob only matching `*.bin`, not the new `*.txt` capture).

**Verified live, end to end, through the real application** — not just the
library: `--connect-maxx-usb MX000000`, then confirmed over the agent API
rather than trusted from a window title. `GET /state` → `"mode":"live",
"signature":"MaxxECU"`; `GET /channels` → 62–70 real named channels
(`TPS input voltage`, `Coolant temp`, `Battery voltage`, `RPM`, `Lambda` with
role `Mixture`, …) depending on what happened to be moving when the ECU was
listened to; `GET /values?channel=Battery%20voltage` → a steady 13.79–13.80 V
across 125 samples, matching the bench supply; `RPM` a steady `0`, matching an
engine that is not running. A standalone console probe against the same
classes confirmed it first (70 channels, five clean rounds) before the
in-application wiring was trusted.

**A real bug, found by the gap between "the library works" and "the
application won't connect."** `MaxxUsbSource` discovers its channels *during*
`Open()` — there is no fixed subscription to know them from in advance, unlike
every other source here — but `LiveSession.Start()` checked the channel count
*before* calling `Open()` at all, so every MaxxECU-USB session was refused
with `"No channels to record"` before the cable was ever spoken to. This
was already fixed on `virtual-dyno` (`Adopt()` called again, after `Open()`,
if the list came back empty) and the fix was ported the same way: `_names`/
`_units`/`_digits`/`_columns` stopped being `readonly`, given `= []`
initializers to satisfy nullability, and `LiveSession.Start` now opens the
source before checking rather than after. Caught immediately by a Report()
trace around the exact call that threw — not by guessing.

**Tune read and a real write, proven at the class level, on a real Race.** A
standalone probe against `MaxxTune`/`MaxxTuneDefinitions`/`FtdiEcuTransport`
directly — same "trust the library before trusting the wiring" order as the
telemetry work above. `MaxxTuneDefinitions.Read` against MTune's own
`ecuSettingsDefinitions.xml` resolved 9,977 settings; `MaxxCan.EnableAt`
(added by the code-review fix that replaced its hardcoded `0xCDB2` with a
name lookup) resolved to that exact address, confirming the fix reads the
right byte for the right reason rather than by coincidence. `MaxxTune.Read`
pulled the whole 64 KB blob; `MaxxTune.Write` set "CAN Analyzer Enable" from
0 to 1, `MaxxTune.Verify` confirmed it landed, then both ran again to put it
back to 0 and confirm that too — chosen as the first live write specifically
because the ECU clears that flag on its own the moment the link drops, so
even a crashed test self-heals. `MaxxWriteStatus.Ok` both times, both writes
verified.

**Not proven: reading or writing a MaxxECU's tune through this application.**
`virtual-dyno`'s `ConnectMaxxEcuUsb` reads the tune and adopts it into the
calibration view on connect; the version wired into `main` deliberately does
not yet, since that integration is coupled to view-model state
(`_maxxTuneTrouble`, `AdoptMaxxTune`, `_maxxTables`) that has diverged between
the two histories and needs its own pass rather than a rushed port. The CAN
sniffer (`MaxxCanSource`) is likewise untested against a real bus. Live
telemetry and gauges are what the application itself has proven; the tune
primitives are proven as a library, not yet as the app's own write path —
there is no agent-API `/tune/set` guardrail (rationale, magnitude limits,
dangerous-constant confirmation) in front of a MaxxECU write yet, because
none of that is wired up for this ECU. Anything writing to one today is
calling `MaxxTune.Write` directly and is its own guardrail.

## 16. The live WebSocket stream breaks on a large schema — found, not fixed

Item 3 called the `skipped`-under-backpressure bullet unreproducible and
otherwise clean. A full-channel sweep against rusEFI's raw stream — 1,028
channels, the same count `GET /channels?raw=true` returns cleanly over plain
HTTP — found a real break the earlier, smaller-channel-count testing (Speeduino
at 181, MaxxECU at ~70) had no way to hit.

**The schema message breaks the connection at exactly 16,380 bytes, every
time, on real hardware.** Confirmed via a raw client (not this application) so
the failure is isolated to the wire, not to anything the app does with the
reply: connect, read the first fragment (16,380 bytes, `EndOfMessage=false`,
socket state still `Open`) — the second fragment throws
`WebSocketException: "An internal WebSocket error occurred"` and leaves the
socket `Aborted`. `LiveSubscriber`'s own exception handling
(`catch (WebSocketException) { }`) swallows this silently, so from inside the
application this looks like a subscriber that connected and then went quiet —
there is nothing in any log to say why.

**Two fixes were tried and both failed to change the outcome:**

- Chunking the write in application code (`LiveSubscriber.Write`, explicit
  `SendAsync` calls with `endOfMessage:false` then `true`) hit the identical
  failure on its own second call, at whatever chunk size was tried (8,192
  included) — the continuation frame itself is what breaks, not the size of
  what is in it.
- Raising the accept-time buffer
  (`HttpListenerContext.AcceptWebSocketAsync`'s `receiveBufferSize`, up to its
  own documented maximum of 65,536 — 262,144 throws `ArgumentOutOfRangeException`
  outright, which surfaced as every WebSocket route answering `500` the first
  time this was tried) changed nothing observable: the same board broke at the
  same 16,380 bytes regardless of what buffer size was requested.

Both are reverted; the code is back to a single unchunked `SendAsync` and the
default accept buffer, since neither the chunked write nor the checked-in
comment explaining the buffer size was doing anything a plain revert did not
already do more simply.

**What this actually looks like: a `HttpListenerContext`/HTTP.SYS-backed
WebSocket cannot complete a continuation frame on this machine**, regardless
of what the managed API is asked for — a platform behaviour underneath both
attempted fixes rather than something either was positioned to reach. Fixing
it for real likely means never asking a single WebSocket message to carry
more than 16 KB in the first place — splitting a large schema across more
than one message, most plausibly — which is a wire-protocol change, not
another parameter to try, and is left here rather than rushed.

**Practical impact, and the workaround used for the rest of this session's
testing:** any firmware whose channel count pushes its schema past ~16 KB —
1,028 channels is comfortably past it; smaller counts are not — cannot use
`WS /live/stream` at all right now, raw or not, default channel set or not,
since rusEFI's *default* schema is the same 1,028 channels as its raw one (no
curation is happening for this firmware — confirmed via `GET /channels`
without `?raw=`, also 1,028). `GET /channels`, `GET /tune`, `GET /tune/full`
and `GET /values` are unaffected — they are plain HTTP responses, not carried
over this WebSocket at all, and a full read-only sweep of every channel on
every connected board used those instead.

## 17. Every channel, read-only, on all three boards — done, one real bug fixed

Using `GET /channels` + `GET /log/full` on each connected board (the item 16
workaround, and the only path that reaches all 1,028 of rusEFI's channels at
once): Speeduino 81/81 channels, rusEFI 1,028/1,028, MaxxECU 66/66, every one
of them present with at least one sample in a three-second window.

**A second real bug, found in the process of checking every channel rather
than the handful item 14 already had:** `GET /values` and `GET /log/full` both
answered `500 "the application failed to answer"` for rusEFI — for *every*
channel in the request, not just the one at fault — because rusEFI's
`Lua: torque` (a user-scriptable calculated channel; this one divides by RPM,
and the bench engine's RPM is 0) reads `NaN`, and the agent API's JSON
serializer refuses non-finite numbers by default. Found by binary-checking
all 1,028 channels individually against `/values` until one produced the
500 that had already been seen from `/log/full`; the .NET exception message
named the exact fix it wanted (`JsonNumberHandling.AllowNamedFloatingPointLiterals`),
applied to both `AgentServer`'s and `LiveSubscriber`'s `JsonSerializerOptions`
so the same fix covers `GET /values`/`GET /log/full` and the live WebSocket
frame path together, rather than only the one that happened to get tested.
`NaN`/`Infinity` are now returned as literal (unquoted) tokens rather than
crashing the response — not strict RFC 8259 JSON, but the value itself
(“this calculated channel is dividing by zero right now”) is real information
a caller is better off seeing than a silently-substituted 0 or null. Verified
live: `GET /values?channel=Lua%3A%20torque` now answers `200` with a run of
literal `NaN`s; the full rusEFI sweep above is with this fix in place.

---

## Not hardware, still open

- **rusEFI `[PcVariables]` axes** — one table and two curves want axes that live
  in TunerStudio rather than on the controller, so they open blank
- Four low-severity review findings: `Fahrenheit` on a `"temp"` unit,
  `commandButton` drawn as inert text, `SettingsMenuEntry.Condition` parsed and
  never evaluated, and the Wi-Fi route disposing a source the view model holds
