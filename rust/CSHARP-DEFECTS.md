# Defects found in the C# while porting it

Porting agents carry the C#'s behaviour over exactly, bugs included, so the two apps
agree. The bugs are collected here so they get fixed once, in both, and are not lost
in the agents' reports. Each one names where it is and how it was found.

| Where | Defect | Found by |
|---|---|---|
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
