# Defects found in the C# while porting it

Porting agents carry the C#'s behaviour over exactly, bugs included, so the two apps
agree. The bugs are collected here so they get fixed once, in both, and are not lost
in the agents' reports. Each one names where it is and how it was found.

| Where | Defect | Found by |
|---|---|---|
| **`MaxxTune.Write` (HIGH, shipping app)** | `TryAsk` doesn't discard input after its last failed attempt, and `Write` doesn't discard before sending the header, so a late telemetry reply's leading 0x80 can be read as the header ack. The real header ack is then taken as the payload ack, and **the write reports success even if the ECU refused the payload**. `Verify` discards first and would catch it, but only if a caller runs it. | w4-live review |
| **`TunerStudioSource.Recover` + `EcuConnection` (HIGH, shipping app)** | A port reopen between `WriteTunePage` and `BurnPage` (a poll-thread recovery) resets a Speeduino, so the written change is lost, and the burn then commits the unchanged tune **and reports success**. The read-back ran before the reset, so it can't catch it. Recovery also reads straight after the reset with no boot wait, into the bootloader. **Fixed in the C# on branch `fix-burn-after-reopen` (aecc523, 3c1896d), not yet merged to main.** | w3-connection review |
| `ChannelClassifier` (units check) | `Simplify` removes `/` and `°`, so the `"g/s"`, `"kg/h"`, `"lb/min"`, `"km/h"` and `"m/s"` arms can never match; only the slash-free spellings work. `"°"` simplifies to empty and is accepted for every role. | w1-channels |
| `SafeFolder` | A safe folder at a filesystem root rejects every file: `Folder + separator` already ends in a separator. | w0-base |
| `MathExpression` | Long runs of unary `-`, `!` or `^` are not depth-limited (only parenthesised nesting is capped at 64), so a hostile expression can overflow the stack. | w0-base |
| `TuningMath.Clamp` use | `Math.Clamp` throws when min > max; the Rust returns NaN instead, since bad input must not crash. | w0-base |
| `SniperSession` | Node id read as `CanId & 0x7FF`; packing is `node << 3`, so it should be `(CanId >> 3) & 0x7FF`. Confirmed on hardware 2026-09-19. | hardware |
| `SniperCanTransport` | `PIPE_TRANSFER_TIMEOUT` never set, so a read on a quiet bus blocks forever. Confirmed on hardware. | hardware |
| `SettingsStore` setters | Each setter changes the field before persisting, outside the lock, so a concurrent save (the poll thread writes batching notes) can write a half-updated state. The Rust holds the lock across change and write. | w1-stores |
| `MaxxUsbProtocol.Request` | Builds any command, including 0x2F, which erases the ECU flash; it was safe only because no caller passed it. The Rust refuses 0x2F outright. | w1-ms-maxx review |
| `DialogCondition` | A parenthesised ternary `(a ? b : c)` evaluates the group with `Or()` and comes out Unknown, while the same ternary without parentheses works, so some INI dialog fields may show or hide wrongly. | w1-ini |
| `FilterStore.Reload` | A `null` element in filters.json throws a NullReferenceException and the whole file is lost; the Rust skips the element. Non-finite bounds throw on save only after the in-memory list has changed (ported in the same order). | w2-filters |
| `ChannelUnits.IsLambda` | The `":1"` case can never match: `ChannelRoles.Simplify` strips colons first, so `":1"` becomes `"1"` and falls through to the value guess. | w2-math-channels |
| `DefinitionImport` ledger (minor) | Unverified against .NET: a character outside the BMP may encode to one `?` in .NET Latin-1 but two in Rust, and a backtick may be escaped differently in the ledger JSON. Both read back fine; neither has been seen in a real definition. | w2-definitions review |
| `Obd2Faults.Parse` | Its comment says a zero byte pair is padding and the rest is ignored, but the code only skips that one pair. | w2-obd |
| `SsmParameterFile` | A `null` entry in `parameters` throws a NullReferenceException; the Rust skips it. | w2-obd |
| `RealtimeDecoder` | A negative field offset throws; the Rust returns NaN for that field. Tune settings with keys differing only in case throw; the Rust keeps the last. | w2-obd |
| `FtdiEcuTransport.Find` | With no MaxxECU present it falls back to the first FTDI device of any kind, so picking MaxxECU USB with a MicroSquirt on an FTDI cable would open the MicroSquirt and send it MaxxECU frames. The Rust only ever opens a device positively identified as a MaxxECU. | w2-transports review |
| `SerialPortNames` (by comparison) | The C# WMI query saw only present devices; a port number reused by a new device was labelled correctly. Keep it that way: the Rust registry rows apply only to ports the live list reports as Bluetooth or unknown. | w2-transports review |
| `Elm327Source.Recover` | The comment says the "died batching" suspicion must survive a failed proving read, but `Watching` overwrites it with the post-reset value, so the evidence is lost. | w3-connection |
| `SsmSource.Read` | Once every address has been dropped it returns NaN rows with no error, so a dead link looks healthy. | w3-connection |
| `EcuConnectionSettings.RetryPause` | Never read. | w3-connection |
| `EcuConnection.Dispose` | Can close the port mid-write or mid-burn, and `ReadIdentity` could reopen the port after Dispose. The Rust waits for the conversation in flight instead. | w3-connection review |
| `MsqWriter` round-trip check | Its round-trip check rounds ties to even while the page encoder rounds half away from zero, so the two can disagree on an exact .5 and the check can pass or fail on the wrong value. | w4-tune |
| `TuneCompare` | Counts NaN cells as differing, even between identical tunes. | w4-tune |
| `EcuTune.Scalars()` | Reads through the declared scale, not the resolved one `Scalar()` uses, so an expression-scaled constant (e.g. `{fuelLoadRes}`) can show differently in the two. | w4-tune |
| `LiveSession` recording | A recording write error (e.g. a full disk) counts as an ECU failure but the failure count resets each poll, so the session loops unpaced and never ends. | w4-live |
| `LiveSession.Snapshot` | Cached by sample count, which stops changing once the view is full, so the snapshot goes stale. | w4-live |
| `LiveSession` stop | Stopping during a reconnect sets the "did not come back" error. | w4-live |
| `MaxxUsbSource` status | ECU status bytes 0 and 1 are read as the local NoAnswer and Uncertain states. | w4-live |
| `MaxxCanSource` | If the drop count is unavailable at open it is never counted. | w4-live |
| `MaxxTune` write | `offset + length` can overflow int before the ceiling check; the Rust checks in 64 bits. The C# ceiling test (offset ceiling-2, length 4) would still pass with the ceiling one higher. | w4-live |
| `TuningProject.With(TuneVersion)` | Moves a replaced version to the end, so re-burning an old version makes it "latest" and the parent of the next version, breaking the documented oldest-first order. | w5-projects |
| `AgentCommands` (batches) | Magnitude is measured against the value after earlier batch items, and the raw items are applied rather than the deduplicated diff, so `[{X: v+0.5r}, {X: v+r}]` moves a value its full range for one rate-limit count. Batch size is unbounded. | w5-agent review |
| `/definitions/import` | An agent can install an INI, which defines the ranges, the DangerousConstants names and the write commands, so a doctored file with the right signature defeats the gates after the next connect. `mustMatch` is empty when nothing is connected. | w5-agent review |
| `AgentServer` | No Host or Origin check (DNS rebinding); the token is accepted in the query string; a `null` batch entry gives a 500; a bare `?raw` is not true; the server leaks if the token file fails to write; the MCP server sends the token to any address in the file; a non-string `method` crashes it; WebSocket frames over 16 KB break. | w5-agent |
| `AgentCommands` (rpm) | The idle gate lets a write through when rpm is unknown. | w5-agent |
