# Live connection

Reading an ECU, or a car, as it runs.

- [What a live session is](#what-a-live-session-is)
- [Supported controllers](#supported-controllers)
- [Definition files](#definition-files)
- [Connecting](#connecting)
- [Reconnecting](#reconnecting)
- [Recording](#recording)
- [Logging rate](#logging-rate)
- [Reading the block in one request](#reading-the-block-in-one-request)
- [Gauges](#gauges)
- [What a session sends](#what-a-session-sends)
- [Losing the link](#losing-the-link)
- [Troubleshooting](#troubleshooting)

---

## What a live session is

A live session is an ordinary log that is still being written. The channel
sidebar, data filters, calculated channels, presets, the heat table and VE
calibration all work on it exactly as they do on a file.

Channels take the names your recorded logs use, so a preset or a filter saved
against a file applies to the ECU too.

## Supported controllers

| Controller | Connection | Needs a definition file? |
| --- | --- | --- |
| MegaSquirt, MicroSquirt | Serial / USB tuning cable | Yes |
| rusEFI | Serial / USB | Yes |
| Speeduino | Serial / USB | Yes |
| MaxxECU | Serial / USB | No â€” its own protocol |
| Any OBD2 vehicle | ELM327 adapter: USB, Bluetooth LE or Wi-Fi | No â€” see [OBD2](OBD2) |
| Subaru, over SSM | Serial / USB | No, but you supply an address list â€” see [Subaru SSM](Subaru-SSM) |

## Definition files

### What they are and why they are needed

A **definition file** â€” a TunerStudio `.ini` â€” describes exactly what an ECU's
data means.

A live ECU sends a block of raw numbers and nothing else: no names, no units, no
scaling. All of that lives in the `.ini` for that exact firmware build.

> **NOTICE:** **Using the wrong definition does not fail.** Firmware versions
> move channels around inside the realtime block, so decoding with the wrong one
> reads every channel from the wrong offset and returns numbers that look
> entirely reasonable. Even adjacent versions count as no match. A session is
> **refused** when no definition matches the signature the ECU reports.

### Where they are looked for

In this order:

1. `%USERPROFILE%\OpenLogViewer\ECU definitions` â€” and its sub-folders
2. `%USERPROFILE%\.efiAnalytics\TunerStudio\config\ecuDef`
3. `Documents\TunerStudioProjects` â€” each project folder

Your own folder is searched first, deliberately: a file you went to the trouble
of putting there is a more deliberate answer than one a tool cached at some point
in the past.

### Adding one

**Tools â–¸ ECU definition filesâ€¦** opens the folder and creates it if it does not
exist. A note is written into it naming the signature your ECU actually reported,
which is the one piece of information that makes finding the right file possible.

| Firmware | Where to get the `.ini` |
| --- | --- |
| MegaSquirt | In the firmware download from msextra.com, or already on the machine if TunerStudio is installed |
| Speeduino | In the Speeduino firmware download, or from SpeedyLoader |
| rusEFI | Published with each build; the rusEFI console can also save the one matching your board |

Nothing is downloaded. OpenLogViewer never uses the internet for this â€” the
folder is how you give it a definition it does not already have.

See [Firmware definitions and channels](Firmware-definitions-and-channels) for how a file is
read and how its channels are mapped.

## Connecting

1. **Tools â–¸ Connect**, or **Connect â–¾** in the toolbar.
2. Pick a device from the list.

The menu is built each time it opens, because an adapter can appear or vanish
while the application is running. It lists:

- Serial ports, named by what is on them where that is known
- Bluetooth LE adapters, with `(Bluetooth LE)` after the name
- **Connect as an OBD2 adapter** â€” for a generic cable
- **Connect to a Wi-Fi OBD2 adapter** â€” see [OBD2](OBD2)
- **Connect over SSM (Subaru)** â€” see [Subaru SSM](Subaru-SSM)

**What happens:** the ECU is asked what it is, the matching definition is found,
and a session starts.

**Expected result:** the toolbar reports the connection, for example:

```text
â— COM9 Â· MS3 Format 0569.00 Â· 15.7 Hz
```

Port, firmware, and the rate. The dot means it is recording. Hover it for the
build string, the definition matched, the channel count, and the file being
written. Retry counts appear only once there are any.

**To verify:** switch to **Gauges** and confirm the readings move as expected â€”
battery voltage between about 12 V and 14.5 V, coolant temperature climbing
through warmup.

> **NOTICE:** **Open the tune before connecting** if you want the channels the
> firmware derives from tune settings. Duty cycle divides by the cylinder count,
> and that does not come over the wire.

Connecting is never automatic. Launching the application does not take a serial
port on its own, which would fight TunerStudio for it and start a session nobody
asked for.

## Reconnecting

**Devices are remembered by hardware id, not by COM port**, because Windows hands
port numbers out and reuses them â€” the same Speeduino is COM7 today and COM12
tomorrow depending what was plugged in first.

What answered on each device is remembered too, since Windows names the chip
rather than the ECU: a Speeduino shows up as "Arduino Mega 2560".

| Route | |
| --- | --- |
| Toolbar **Connect: *your ECU*** | The last device used |
| **Ctrl+K** | The same |
| **Connect â–¾** | Devices that have answered before are grouped above the rest, most recently used first |

**Tools â–¸ Forget remembered ECUs** clears the device list and nothing else.
Presets, filters and calculated channels are your work and are not swept up in
it. Useful when a dongle is sold or an adapter replaced and its name keeps
appearing in a list of things that are no longer there.

## Recording

**Connecting does not record on its own.** A session is opened to check a link,
read a gauge or watch a change far more often than to capture anything, and
recording every one of those buries the run that mattered among the ones that did
not.

| Control | Default | What it does |
| --- | --- | --- |
| Toolbar **â— Recordâ€¦** / **â–  Stop recording** | â€” | Starts and stops a recording. You choose the moment, the name and the folder |
| **Tools â–¸ Record as soon as I connect** | **Off** | When on, connecting starts a recording immediately |
| **Tools â–¸ Open the recordings folder** | â€” | Opens `%USERPROFILE%\OpenLogViewer\Logs` |

**Expected result:** the status bar reads `REC 1,204 rows` while recording and
`not recording` otherwise. Connecting says which of the two it is doing.

The state is stated rather than left to be inferred, because what the default
costs is a run somebody meant to capture.

### What a recording is

- A log in its own right. Its clock starts **where you pressed record**, not
  where the session began, so it opens without twenty minutes of nothing in front
  of it.
- Written continuously. Every row is flushed as it arrives, so a pulled cable
  leaves a complete file.
- Named `live-<yyyy-MM-dd_HH-mm-ss>.csv` by default, so a folder of them sorts
  into the order they happened.
- One session can produce as many files as you like.

The next **Save as** opens wherever the last recording went, rather than back at
the workspace every time.

### What the plot does while recording

The plot follows the newest data, and **stops following as soon as you zoom or
pan** â€” from then on you are reading history. **View â–¸ Reset zoom** goes back to
watching.

**Hide unused** is on by default, so on a bench with the engine off almost
everything is hidden. Everything is still being recorded.

## Logging rate

**Tools â–¸ Logging rate.**

| Rate | Notes |
| --- | --- |
| 5 Hz | |
| 10 Hz | |
| **25 Hz** | **Default** |
| 50 Hz | |
| 100 Hz | |
| 200 Hz | |

25 Hz is past what a wideband can actually resolve, and enough for fuelling work
where the wideband is the slow part. The faster rates are for transients â€” accel
enrichment, knock, per-cylinder events â€” and cost what they sound like they cost:
at 100 Hz a rusEFI's 823 channels are about 14 MB a minute on disk.

The rate is a ceiling, not a promise. A link that cannot go that fast goes as
fast as it can; the toolbar reports the rate actually achieved.

## Reading the block in one request

**Tools â–¸ Read the block in one request.** Default **off**.

Asks the firmware for the whole realtime block in a single request instead of
several.

> **CAUTION:** Faster where the firmware allows it, **fatal where it does not**.
> Some firmware answers a request larger than its own page size with garbage or
> not at all. Turn it on, confirm the readings are still sane, and turn it back
> off if they are not.

## Gauges

Switch to **Gauges** in the toolbar, or **View â–¸ Gauges**.

The dials are the ones the **firmware itself defines**, with its own ranges and
its own warning bands where it declares them.

**A dial with no face:** some gauges are defined with their bounds left at zero.
They are kept rather than dropped â€” the channel is worth seeing even when the
firmware has not said what a normal value looks like â€” and show as a reading
without a scale.

OBD2 gauges are drawn differently; see [OBD2 â–¸ Gauges](OBD2#gauges).

## What a session sends

**Reading is what a session does; writing takes asking for.**

Connecting sends only the commands that:

- ask what the firmware is
- read the realtime page
- read the settings

â€” the same things TunerStudio reads continuously. **Nothing is written unless you
edit a table or a setting and press the button.** VE calibration suggests a table
rather than applying one.

See [Editing a tune](Editing-a-tune) for what happens when you do write.

## Losing the link

Key off and key on is normal, so **a lost link does not end the session**.

**What you see:** the connection indicator goes hollow and amber.

The link is waited on for **one minute**. When the ECU comes back, the session
carries straight on into the same recording.

## Troubleshooting

| Symptom | Likely cause | What to check |
| --- | --- | --- |
| The device is not in the **Connect â–¾** list | It is a Bluetooth LE adapter that has not paired, or a Wi-Fi dongle | BLE adapters appear with `(Bluetooth LE)`; Wi-Fi dongles appear in no list â€” see [OBD2](OBD2) |
| "No definition file on this machine matches it" | The `.ini` for that exact firmware build is missing | The message names the signature the ECU reported. Put the matching `.ini` in `%USERPROFILE%\OpenLogViewer\ECU definitions` |
| "The ECU did not say what it is" | Wrong baud rate, wrong cable, or nothing on the port | Confirm the port, and that TunerStudio is not holding it |
| The port cannot be opened | Another program has it | Close TunerStudio or any other tuning software |
| Readings look plausible but are wrong | The wrong definition matched | Compare a known value â€” battery voltage, coolant â€” against a gauge you trust |
| Channels the firmware derives are missing | No tune was loaded before connecting | Open the tune, then reconnect |
| The rate is far below the one selected | The link cannot go that fast | The toolbar reports the achieved rate. Lower the setting to match |
| The session drops the moment it starts | **Read the block in one request** is on and the firmware cannot do it | Turn it off under **Tools** |
| Opening the port resets the board | Normal on an Arduino-based board such as a Speeduino | Unburned changes are lost by the reset. Burn before reconnecting |

## Related

- [OBD2](OBD2) â€” standard vehicles through an ELM327
- [Subaru SSM](Subaru-SSM)
- [Editing a tune](Editing-a-tune)
- [Firmware definitions and channels](Firmware-definitions-and-channels)
- [Configuration](Configuration)
