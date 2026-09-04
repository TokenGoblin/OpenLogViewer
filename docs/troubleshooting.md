# Troubleshooting

Organised by what you actually see. Each entry gives the likely cause and what to
check.

- [Where to look first](#where-to-look-first)
- [Installing and starting](#installing-and-starting)
- [A log will not open](#a-log-will-not-open)
- [The log opens but looks wrong](#the-log-opens-but-looks-wrong)
- [Connecting to a controller](#connecting-to-a-controller)
- [OBD2 adapters](#obd2-adapters)
- [Recording](#recording)
- [Histogram and VE calibration](#histogram-and-ve-calibration)
- [Editing a tune](#editing-a-tune)
- [AI agent (MCP)](#ai-agent-mcp)
- [Settings and files](#settings-and-files)

---

## Where to look first

Three places, in order:

1. **The message itself.** Errors here name what was wrong rather than saying
   something failed. A definition mismatch names the signature the ECU reported;
   a log that will not open names what was wrong with the file.
2. **`%TEMP%\openlogviewer-run.log`.** This is where a scripted run reports
   failures, and where unhandled exceptions are recorded. A GUI application has no
   console, so nothing is printed to one.
3. **The hover text on the connection indicator**, which carries the build
   string, the definition matched, the channel count and the file being written.

## Installing and starting

| Symptom | Likely cause | What to check |
| --- | --- | --- |
| "Windows protected your PC" on the installer | The installer is not code-signed | Expected. **More info ▸ Run anyway** |
| The application will not start on an older Windows | Below Windows 10 build 17763 | Check the Windows version. There is no build for earlier releases |
| A new version installs alongside the old one | A four-part version was used | Releases must use a three-part version. Uninstall both and install a three-part build |
| The window vanishes with no error | An exception on a background thread | `%TEMP%\openlogviewer-run.log` records it |

## A log will not open

| Symptom | Likely cause | What to check |
| --- | --- | --- |
| "… is not a recognised datalog format" | No reader would accept the file | Open it in a text editor. Delimited text needs a header row followed by numeric data |
| "No consistent column delimiter found" | The rows do not split the same way | Check the file uses one delimiter consistently on every row |
| "Could not locate a header row followed by numeric data" | The header and the data do not line up | Check for preamble rows before the header, or non-numeric columns throughout |
| "File is empty" | Zero bytes, or nothing but blank lines | Check the file actually contains the recording |
| Every number is wrong by a factor of ten | Decimal comma read as a thousands separator, or the reverse | Confirm the file's decimal separator is consistent |
| Accented characters are mangled | The file is ISO-8859-1 and was read as UTF-8, or the reverse | Both are handled; report the file if it is not |
| The log opens but has no time axis | No usable time column | A time base is synthesised from the sample index. Values are then in samples, not seconds |
| A renamed file will not open | *(This should not happen)* | Nothing is assumed from the extension — content is examined. Report the file |

If a log from an ECU not listed in the [supported
formats](user-guide.md#supported-log-formats) does not open, the format is usually
easy to add — the delimited reader is one file.

## The log opens but looks wrong

| Symptom | Likely cause | What to check |
| --- | --- | --- |
| Most channels are missing from the list | **Hide unused** is on and those channels never move | Turn it off. They are still recorded and still exported |
| A trace is a flat line at its own mid-height | The channel barely moves, and steady channels are drawn as steady | **View ▸ Draw steady channels as steady**, turn it off |
| A trace has holes in it | Logging was paused, or a calculated channel has non-finite results | Gaps are drawn deliberately. The pen lifts when a step exceeds ten times the median sample interval |
| Everything is squashed against the axis | One plotted channel reaches a huge value | Pin a scale on the offending channel, or `clamp(…)` it if it is calculated |
| Two traces are the same colour | A pinned colour is in use | Pinned colours are not re-picked. Right-click ▸ **Back to automatic** |
| A duplicate channel name appears twice | The firmware emits it twice in different units | Expected — MS3 emits `Fuel Consumption` in both GPH and l/hr. They are disambiguated by units |
| A calculated channel has no data | An input channel is not in this log | The sidebar reports which definition did not fit |

## Connecting to a controller

| Symptom | Likely cause | What to check |
| --- | --- | --- |
| "The ECU reports … and no definition file on this machine matches it" | The `.ini` for that exact firmware build is missing | The message names the signature. Put the matching `.ini` in `%USERPROFILE%\OpenLogViewer\ECU definitions` |
| "The ECU did not say what it is" | Wrong cable, wrong port, or nothing is powered | Confirm the port and that the ECU is powered |
| The port cannot be opened | Another program holds it | Close TunerStudio or any other tuning software |
| The device is not listed at all | It is Bluetooth LE, or a Wi-Fi dongle | BLE adapters show `(Bluetooth LE)`. Wi-Fi dongles appear in no list — see below |
| Readings look plausible but are wrong | The wrong definition matched | Compare battery voltage or coolant against a gauge you trust. Firmware versions move channels inside the block, so a wrong definition produces reasonable-looking nonsense |
| Channels the firmware derives are missing | No tune was loaded before connecting | Duty cycle divides by the cylinder count, which does not come over the wire. Open the tune, then reconnect |
| The rate is far below the one selected | The link cannot go that fast | `liveRate` is a ceiling, not a promise. The toolbar reports the achieved rate |
| The session dies the moment it starts | **Read the block in one request** is on and the firmware cannot do it | **Tools ▸ Read the block in one request**, turn it off |
| The board resets when connecting | Normal on Arduino-based boards such as a Speeduino | Unburned tune changes are lost. Burn before reconnecting |
| The indicator goes hollow and amber | The link was lost | This is normal for key-off. It is waited on for 60 s and the session resumes into the same recording |

## OBD2 adapters

| Symptom | Likely cause | What to check |
| --- | --- | --- |
| A Bluetooth adapter never becomes a COM port | It is Bluetooth LE, which has no serial port profile | It never will. Look for it in **Connect ▾** with `(Bluetooth LE)` after the name |
| A Wi-Fi adapter appears nowhere | Correct — it is an access point, not a device | Join its Wi-Fi network, then **Connect ▾ ▸ Connect to a Wi-Fi OBD2 adapter** |
| A Wi-Fi connection times out | Windows left the dongle's network for one with internet | Windows treats a network with no route to the internet as a mistake and moves off it, often within seconds. Re-check the network list |
| A Wi-Fi connection is refused | These adapters accept one connection at a time | Close the phone app still holding it |
| "Nothing on COM*n* answered as an OBD2 adapter" | Wrong port, or it is a tuning cable | Try **Connect ▾ ▸ Connect as an OBD2 adapter** for a generic `USB-SERIAL CH340` |
| Connected, but no channels | Ignition off, or the vehicle is not answering on the bus | Turn the ignition on. Some vehicles need the engine running |
| The link dies about a second after connecting | The adapter cannot survive a batched request | It is recorded after two occurrences and not probed again. A different adapter starts clean |
| It is very slow, around 2 Hz | That is OBD2 | Each parameter is a separate request. There is no realtime block in the standard |
| Gauges stop while reading fault codes | The adapter takes one command at a time | Expected. Wait a second or two |
| A fault code has no description | It is in a manufacturer-specific range | P1131 means one thing on a Ford and something unrelated on a Toyota. The window says so rather than guessing |

## Recording

| Symptom | Likely cause | What to check |
| --- | --- | --- |
| Connecting did not start a recording | **Record as soon as I connect** is off, which is the default | Press **● Record…**, or turn the option on under **Tools** |
| The recording is empty | Recording was never started | The status bar reads `REC <n> rows` while recording and `not recording` otherwise |
| The recording is missing the start of the session | Its clock starts where you pressed record | This is intended, so a file does not open with twenty minutes of nothing in front of it |
| The recordings folder is not where expected | The data folder was changed | **File ▸ Data folder ▸ Open the folder** |
| A recording is incomplete after a pulled cable | *(This should not happen)* | Every row is flushed as it arrives. Report it if a file is truncated |
| The plot stopped following live data | You zoomed or panned | From then on you are reading history. **View ▸ Reset zoom** goes back to watching |
| Almost nothing is shown on a bench | **Hide unused** is on and the engine is off | Everything is still being recorded |

## Histogram and VE calibration

| Symptom | Likely cause | What to check |
| --- | --- | --- |
| The table is nearly empty | Filters are excluding most samples | The status line reports how many were excluded |
| The table looks evenly populated across the whole map | The axes are probably wrong | Turn on **Colour by sample count** and check which cells the drive really visited |
| Two tables will not compare cell for cell | The axes re-scaled to the surviving samples after filtering | Differently-filtered tables are not directly comparable. Use the tune's own axes for a fixed grid |
| The table does not line up with TunerStudio | Uniform bins are being used | Pick one of the tune's own tables under **Axis breakpoints** |
| **Suggest a new fuel table** is greyed out | The axis source is not a tune table | Pick a tune table under **Axis breakpoints** |
| Every cell reads "thin" | Not enough samples per cell | Lower **Min samples**, use fewer bins, or log a longer drive |
| Corrections all sit at the clamp | Measured and target are different quantities | Both must be AFR, or both lambda, referenced to the same fuel |
| Corrections smear across a region of the table | Wideband delay is not set | Press **Find it** |
| **Find it** says the engine did not change enough | A steady-state log | Every delay pairs a cell with readings from the same conditions, so they all score alike. Log a drive with ramps |
| The suggested table looks nothing like the tune | The opened tune is not the one the log ran | **File ▸ Use the tune stored in the log** |
| An axis was rejected | It is one of the rolled `…doz` variants, which are not stored in order | Handled deliberately. Pick a different table |

## Editing a tune

| Symptom | Likely cause | What to check |
| --- | --- | --- |
| **Calibration** shows nothing | Not connected, or connected over OBD2 | A standard vehicle has no tune. It shows fault codes instead |
| **Send to ECU** is greyed out | Nothing changed, or the tune came from a file | A tune opened from a file cannot be sent. Use **Tools ▸ Restore a saved tune to the ECU…** |
| **Burn** is greyed out | Nothing has been sent, or that page declares no burn command | Send first |
| The confirmation counts far more cells than expected | A scale was applied to a larger selection than intended | `Esc` restores the selection. A table scaled by 5 % is 256 changes |
| Changes are gone after the key was switched off | They were sent but not burned | Sending lands in RAM and takes effect immediately; only a burn is permanent |
| Changes are gone after reconnecting | The port opening reset the board before a burn | Burn before disconnecting |
| A value will not accept what you type | The firmware's declared range is tighter than the storage | The declared range is enforced. MS2Extra declares −10 ° to 90 ° for ignition where the encoding would take ±3,276 ° |
| A restore left settings unchanged | The file never mentioned them | Reported as **Missing** in the plan. They keep the ECU's values, which is the point |
| A restore reported rejected settings | The file's value is not storable by this firmware | Listed in the plan. Those settings keep the ECU's values |
| "This firmware definition declares no settings pages" | The matched `.ini` has no page definitions | Confirm it is the full firmware `.ini` |
| A settings field shows **?** | Its condition could not be evaluated | Shown rather than hidden deliberately, so you can tell an unexplained setting from an unreachable one |

## AI agent (MCP)

| Symptom | Likely cause | What to check |
| --- | --- | --- |
| The client reports `ConnectionRefused` | The server is not armed | This is the design working. **AI agent ▸ Allow an AI agent to connect (MCP)** |
| Arming fails | A second copy of the application already holds port 7071 | Hover the menu item for the full status line, which says why |
| The server is off again after a restart | It is off at every launch and never remembers being on | Deliberate. There is no setting that starts it armed. `--mcp` arms it for one run |
| An agent's write seems to hang | It is waiting for you to answer the confirmation in this window | Answer the dialog. The call does not return until somebody does |
| The agent cannot clear fault codes or restore a tune | Neither is offered as a tool | Deliberate. Both are done by a person — see [mcp-server.md](mcp-server.md) |

Full detail: [AI agent access (MCP)](mcp-server.md).

## Settings and files

| Symptom | Likely cause | What to check |
| --- | --- | --- |
| Presets and filters are gone after reinstalling | *(This should not happen)* | Both live in `%APPDATA%\OpenLogViewer` and survive uninstall. Check the folder |
| A new install has nothing in it | A new install is a blank slate by design | Copy the five JSON files from `%APPDATA%\OpenLogViewer` on the old machine |
| A hand edit to a settings file was lost | The application was running and overwrote it | Close the application first |
| A hand edit had no effect | For `units`, the value is matched case-sensitively | Use `AsReported`, `Metric` or `Imperial` |
| A settings file is ignored entirely | It is malformed | A malformed file is treated as absent rather than stopping the application. Validate the JSON |
| Recordings are being uploaded to OneDrive | The data folder was moved into a redirected folder | Move it back out. `%USERPROFILE%` is not redirected |
| A device keeps appearing that is no longer here | It is in the remembered list | **Tools ▸ Forget remembered ECUs**. Presets, filters and calculated channels are untouched |

## Still stuck

Open an issue at
<https://github.com/TokenGoblin/OpenLogViewer/issues> with:

- What you did, and what happened instead
- The exact message text
- `%TEMP%\openlogviewer-run.log` if a scripted run was involved
- The ECU and firmware version, or the adapter model, for a connection problem
- A sample log if a file will not open
