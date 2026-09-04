# Editing a tune

Reading, changing and saving the tune on a connected controller.

- [Safety first](#safety-first)
- [What a tune is](#what-a-tune-is)
- [Opening the Calibration view](#opening-the-calibration-view)
- [Editing a table](#editing-a-table)
- [Editing the settings](#editing-the-settings)
- [Send and Burn](#send-and-burn)
- [Saved tune files](#saved-tune-files)
- [Restoring a saved tune](#restoring-a-saved-tune)
- [What has been verified](#what-has-been-verified)
- [Limitations](#limitations)
- [Troubleshooting](#troubleshooting)

---

## Safety first

> **WARNING:** A write takes effect **immediately on a running engine**. An
> incorrect fuel or ignition value can cause detonation, overheating or piston
> damage within seconds. Make changes with the engine stopped where you can, and
> in small steps with the engine monitored where you cannot.

> **WARNING:** **Burn with the engine stopped.** The ECU pauses while it writes
> flash. A burn on a running engine can stall it or worse. The confirmation asks
> whether the engine is stopped; that question is yours to answer, and nothing in
> software can check it.

> **NOTICE:** Every write is read back and compared before it is called done. A
> write reported as successful was verified. A write reported as failed may still
> have partially landed — re-read the tune before continuing.

## What a tune is

A **tune** is everything the ECU runs from. It comes in two halves:

| Half | What it is | Example |
| --- | --- | --- |
| **Tables** | Two-dimensional maps indexed by RPM and load | VE, ignition advance, AFR target |
| **Settings** | Individual constants, switches and named options | Rev limit, injector dead time, number of cylinders |

**A tune is mostly not tables.** An MS3 offers 144 settings pages, an MS2Extra
55, a Speeduino 49.

The `.msq` file is TunerStudio's tune format, and the one every other tool in
this world reads.

## Opening the Calibration view

Switch to **Calibration** in the toolbar, or **View ▸ Calibration**, on a live
connection.

**Expected result:** the tables and a **Settings** panel appear. They are read
**off the controller** rather than from a saved file, so they are what it is
running now.

Calibration is not available on an OBD2 connection — a standard vehicle has no
tune to read. It shows [fault codes](obd2.md#fault-codes) instead.

## Editing a table

### Selecting cells

| Key or gesture | What it does |
| --- | --- |
| Click, or click and drag | Select a block of cells |
| Arrow keys | Move the selection |
| Shift + arrows | Extend the selection |

### Changing values

| Key | Change | With Shift |
| --- | --- | --- |
| `+` `−` | Nudge by the firmware's own smallest step | Ten steps |
| `PgUp` `PgDn` | Scale by 1 % | 5 % |
| `Esc` | Put the selection back to what the ECU said | — |

**Scaling is there because it is how tuning is actually done.** A region reading
four per cent lean is corrected by adding four per cent to it, not by typing
sixteen numbers.

### What a changed cell looks like

**A changed cell is outlined, and the header counts them.** The shading still
says what the value *is*; the outline says it is not what the ECU holds.

### Value limits

**Values are held to the range the firmware declares,** which is far tighter than
the storage allows. An ignition table kept as a signed 16-bit tenth of a degree
would accept ±3,276 ° as far as the encoding cares, while MS2Extra declares
−10 ° to 90 °.

## Editing the settings

The **Settings** half of the Calibration view sits beside the tables. Its pages
are built from what the firmware says about itself — the menus, dialogs and
fields the `.ini` declares, which is the same description TunerStudio draws from.

**Nothing about them is written here.** The firmware supplies all of it:

| Field type | How it is shown |
| --- | --- |
| Number | A box, with the units the definition declares |
| Bit field | The list of names the firmware gives its values |
| Name | A text box |
| Read-only | Shown, not editable |

A changed field is outlined the way a changed table cell is.

### Pages change as you edit

**Almost every field is conditional.** "Window Sample Type" means nothing without
knock detection on and set to analogue, and the conditions are written against
the tune's own settings. Turn something on and the fields that configure it
appear.

**Where a condition cannot be worked out, the field is shown with a `?` rather
than hidden.** An unexplained setting is better than one you cannot reach, but you
should be able to tell which you are looking at.

## Send and Burn

**Send and Burn are separate, because they are separate on the ECU.**

| Action | Where it lands | Survives a power cycle? | Takes effect |
| --- | --- | --- | --- |
| **Send to ECU** | Working memory (RAM) | **No** | Immediately, on a running engine |
| **Burn** | Flash | **Yes** | Immediately, and permanently |

That RAM step is a safety feature, not an inconvenience: **a change that turns
out to be wrong is undone by turning the key off.**

### Sending

Press **Send to ECU**.

**Expected result:** a confirmation appears saying **how many cells it is about
to change**, then the write goes out and is read back and compared.

That count matters. A table scaled by 5 % when one cell was meant is 256 changes,
and it looks identical to one change until it is counted.

For settings, the confirmation says **how many settings, how many bytes and how
many pages** — a handful of settings is one write or a dozen depending where they
sit.

### Burning

Press **Burn**.

**Expected result:** a confirmation asks whether the engine is stopped, then the
pages that were written are committed to flash.

**A burn commits only the pages that were written.**

**To verify a burn worked:** power-cycle the ECU and re-read the tune. The
changed values should still be there. Nothing persists without a burn.

Send and Burn work the same way for settings as for tables, and separately from
them.

## Saved tune files

A tune that exists only in an ECU is one power supply away from being gone.

| Command | What it does |
| --- | --- |
| **Tools ▸ Save the tune to a file…** | Writes every setting to a `.msq` — a backup, and the format TunerStudio reads |
| **Tools ▸ Open a saved tune…** | Opens a `.msq` and its firmware's settings pages, with **no ECU attached** |
| **Tools ▸ Compare with a saved tune…** | Says which settings the file and the tune in hand disagree about |
| **Tools ▸ Restore a saved tune to the ECU…** | Writes the settings that differ — see below |

**Saving is refused for a tune opened from a definition file,** whose every value
is a zero standing in for one. Writing that out would produce a file that looks
like a tune and is not.

**A tune opened from a file cannot be sent.** It is a real tune — worth looking
at, worth saving again, worth comparing against what is attached — but sending it
would write every setting the file carries and, for every setting it does not, a
zero. Restoring a whole tune to an engine is a deliberate act, not something that
falls out of having opened a file. Use **Restore**.

## Restoring a saved tune

**Tools ▸ Restore a saved tune to the ECU…**

This is **the largest change this application can make to an engine**, and it is
handled accordingly.

### What you are shown before anything is sent

"Restore this tune" is not a question anybody can answer. "Change 47 settings,
one of them the rev limit, and leave 900 alone" is. So the plan is worked out and
handed to you first:

| Reported | Meaning |
| --- | --- |
| **Differences** | Which settings would change, and to what |
| **Writes / bytes / pages** | How much would actually be sent |
| **Missing** | Settings the firmware declares that the file never mentioned. Left exactly as the ECU has them |
| **Rejected** | Settings the file gave a value this firmware cannot store — an option name it no longer offers, a number outside its range. Left exactly as the ECU has them |
| **Unknown** | Names in the file this firmware has no constant for |
| **Signature check** | Whether the file and the controller agree about which firmware this is |

### What is actually written

**Only what differs, and only what the file actually carried.**

The file is laid over the controller's own bytes rather than over nothing, so a
setting the file never mentioned keeps whatever the ECU has. The two images are
then compared byte for byte, so those settings produce no write at all.

A tune saved from a neighbouring firmware revision is missing a handful of
constants, and **the difference between leaving those alone and writing zeros
over them is the difference between a restore and a wrecked tune.**

> **NOTICE:** A signature mismatch is not fatal on its own — a tune saved from
> revision 3.4.2 and a controller running 3.4.3 have almost everything in
> common — but it is the single fact most worth reading before you send eight
> hundred settings to an engine. The plan states it explicitly.

Restore is **not** available to a connected AI agent. See
[AI agent access (MCP)](mcp-server.md).

`--plan-restore <file.msq>` prints what a restore would change and does none of
it. There is deliberately no command-line flag that carries one out.

## What has been verified

### Reading, on a Speeduino 202501

- The tune reads in **424 ms** — 3,408 bytes across fifteen pages.
- Two consecutive reads are **byte-identical**.
- Values were checked against the raw bytes rather than against the display:
  `0x09` at a scale of a tenth is the 0.9 ms injector open time, `0x5F` is the
  95 % duty limit, `0x61` masked to bits 4–7 is six cylinders.

### Writing, on the same board

- Changing one RPM setting sent a **single byte**. The read-back matched, and
  re-reading the whole tune showed that byte changed and **no other byte in 3,408
  had moved**.
- **The bit field case:** seven settings share byte 83 of page 14. Flipping one of
  them moved exactly one bit — `01001010` to `01001000` — leaving the other six as
  they were.

That last one is the failure this is built to avoid. An ECU takes a clobbered
byte without complaint, and the read-back agrees with what was sent.

### Elsewhere

- Read, write and burn verified on a Speeduino over USB and on a rusEFI board.
- A burn was verified across a physical power cycle.
- A cell edited in OpenLogViewer was confirmed in TunerStudio on a live
  MicroSquirt.

## Limitations

- **Tables and settings only.** Firmware flashing is not supported.
- **No undo across a send.** `Esc` restores the selection before a send; after a
  send, the way back is to send the old values again, or to power-cycle if you
  have not burned.
- **A tune opened from a file cannot be sent** — use Restore.
- **A definition file opened as a tune reads all zeros** and cannot be saved.
- **Nothing persists without a burn.** A sent change is gone at the next power
  cycle.
- **Opening the serial port resets some boards.** On an Arduino-based board such
  as a Speeduino, connecting resets it — so unburned changes are lost.

## Troubleshooting

| Symptom | Likely cause | What to check |
| --- | --- | --- |
| **Calibration** shows nothing | Not connected, or connected over OBD2 | OBD2 vehicles have no tune. Connect to a tuning ECU |
| **Send to ECU** is greyed out | Nothing has changed, or the tune came from a file | A tune opened from a file cannot be sent; use **Restore** |
| **Burn** is greyed out | Nothing has been sent yet, or the firmware declares no burn command for that page | Send first |
| The confirmation says far more cells than expected | A scale was applied to a selection larger than intended | Press `Esc` to put the selection back, then re-select |
| A value will not go where you type it | The firmware's declared range is tighter than the storage | The declared range is what is enforced. Check the setting's limits in the definition |
| Changes disappear after switching the key off | They were sent but not burned | Sending lands in RAM. Burn to make it permanent |
| Changes disappear on reconnecting | Opening the port reset the board before the burn | Burn before disconnecting |
| A restore leaves settings unchanged | The file never mentioned them | The plan reports these as **Missing**. This is intended |
| A restore reports rejected settings | The file's value is not storable by this firmware | The plan lists them. Those settings keep the ECU's values |
| "This firmware definition declares no settings pages" | The `.ini` matched has no page definitions | Confirm the definition is the full firmware `.ini`, not a cut-down one |

## Related

- [Live connection](live-connection.md)
- [VE calibration](ve-calibration.md) — where a suggested table comes from
- [AI agent access (MCP)](mcp-server.md) — what an agent can and cannot do here
- [Firmware definitions and channels](ini-and-channels.md)
