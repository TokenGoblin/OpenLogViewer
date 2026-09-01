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
| **Speeduino** | COM14, Arduino Mega. A bench board. Opening the port resets it, so anything unburned is undone by reconnecting — the safest thing to test on. Launch control is **enabled** at a 2,700 rpm soft limit, so its rev limits are not inert. |
| **rusEFI** | COM8, uaEFI board. Bench, USB power. Reset with `cmd_reset_controller` = `Z\x00\xbb\x00\x00` framed and written straight at the transport. **Nothing else in `[ControllerCommands]` should be sent casually — it also holds `cmd_test_spk1..12`, which fire ignition coils.** |
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

## 2. The derived-scale fix on the other two firmwares

Fixed and proven on a Speeduino only. MS2Extra and MS3 also state scales as
expressions — `{0.01 * (maf_range + 1)}` on the MAF curve — and rusEFI may.

- Read each controller's tune and check every constant with a `ScaleExpression`
  resolves to something other than its declared fallback
- Compare a TunerStudio-saved `.msq` for that firmware against the live tune and
  confirm the only differences reported are real ones

The bug this closes was invisible until a real TunerStudio file was compared
against a live controller. Our own round-trip tests all passed, because they
write files with our own writer at our own wrong scale.

## 3. The agent API's live stream

The whole reason it was built for speed, and it has only ever seen a log.

- Connect, start the API, subscribe over the WebSocket
- Measure the frame rate actually delivered against the poll rate
- Confirm frames are pushed at the ECU's pace rather than the window's
- Force a slow reader and check `skipped` counts rise rather than the poll
  slowing down
- Check the schema is re-sent when the channel set changes

## 4. An agent writing to a real controller

`SetSetting` and `SetTableCell` have only been driven against `FakeController`.

- Arm writes, change one setting through the API, read it back off the ECU
- Confirm the arming clears on disconnect and the next write is refused
- Confirm there is still no way to burn through it

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

---

## Not hardware, still open

- **rusEFI `[PcVariables]` axes** — one table and two curves want axes that live
  in TunerStudio rather than on the controller, so they open blank
- Four low-severity review findings: `Fahrenheit` on a `"temp"` unit,
  `commandButton` drawn as inert text, `SettingsMenuEntry.Condition` parsed and
  never evaluated, and the Wi-Fi route disposing a source the view model holds
