# Configuration

Every setting, its default, its valid values, and where it is kept.

- [Where files go](#where-files-go)
- [What is yours, and what ships](#what-is-yours-and-what-ships)
- [settings.json](#settingsjson)
- [presets.json](#presetsjson)
- [filters.json](#filtersjson)
- [math.json](#mathjson)
- [channels.json](#channelsjson)
- [ssm-parameters.json](#ssm-parametersjson)
- [Settings reachable from the interface](#settings-reachable-from-the-interface)
- [Editing the files by hand](#editing-the-files-by-hand)

---

## Where files go

Two locations, with a clear division: **one holds files you own, the other holds
files the application owns.**

### Your files

```text
C:\Users\<you>\OpenLogViewer\
    Logs\             live recordings, named for when they were taken
    Exports\          where Export starts
    ECU definitions\  firmware .ini files you supply
```

| | |
| --- | --- |
| Default location | `%USERPROFILE%\OpenLogViewer` |
| Change it | **File ▸ Data folder ▸ Change the folder…** |
| Open it | **File ▸ Data folder ▸ Open the folder** |
| Recording name | `live-<yyyy-MM-dd_HH-mm-ss>.csv` |

**Deliberately not "My Documents".** That is redirected into OneDrive on most
machines, which buries recordings a couple of levels deeper and uploads every one
of them *while it is still being written* — a long session is tens of megabytes of
continuous sync over whatever connection the car happens to be near. The user
profile is not redirected, and is one level shorter.

If the chosen folder cannot be written — an unplugged drive, a network share that
is not there — the application falls back to the default location rather than
losing the recording that was about to start.

### The application's files

```text
%APPDATA%\OpenLogViewer\
    settings.json
    presets.json
    filters.json
    math.json
    channels.json
```

`%APPDATA%` is normally `C:\Users\<you>\AppData\Roaming`.

**Nothing is ever written next to the executable,** so the application is content
installed read-only under Program Files.

## What is yours, and what ships

Everything the application remembers about *you* is in those five files, and
**none of it travels with the software**.

**A new install is a blank slate:** no presets, no filters, no calculated
channels, no pinned colours or scales, no remembered devices.

The filters offered when you open a log are generated from the channels *that
log* has and arrive **switched off**, so opening a file never silently changes
what a table counts.

Uninstalling leaves both folders in place. Delete `%APPDATA%\OpenLogViewer` by
hand for a clean slate.

## settings.json

`%APPDATA%\OpenLogViewer\settings.json`

Written camelCase. A missing or nonsensical value takes the default rather than
being honoured.

| Key | Type | Default | Valid values | Description |
| --- | --- | --- | --- | --- |
| `version` | integer | `1` | `1` | File format version |
| `themeId` | string | `midnight` | Any theme id — the lower-case hyphenated scheme name | Active colour scheme |
| `dataFolder` | string | *(unset)* | Any writable path | Where recordings and exports go. Unset means `%USERPROFILE%\OpenLogViewer` |
| `liveRate` | number | `25` | Greater than 0, up to 1000 | Samples per second requested from a live connection, in Hz |
| `singleRequestBlock` | boolean | `false` | `true` / `false` | Ask for the whole realtime block in one request |
| `recordOnConnect` | boolean | `false` | `true` / `false` | Whether connecting starts a recording immediately |
| `recordingFolder` | string | *(unset)* | Any path | Where the last recording was saved by hand, so the next **Save as** opens there |
| `units` | string | `AsReported` | `AsReported`, `Metric`, `Imperial` | Which units readings are displayed in. **Case-sensitive** — an unrecognised value falls back to `AsReported` |
| `knownEcus` | object | *(unset)* | hardware id → name | What answered on each serial device |
| `ecuLastUsed` | object | *(unset)* | hardware id → ISO-8601 timestamp | When each device was last connected to |
| `obd2BatchDeaths` | object | *(unset)* | adapter id → count | How many times a batched OBD2 request killed the link on that adapter |

### Notes on individual settings

**`liveRate`** is a ceiling, not a promise. A link that cannot go that fast goes
as fast as it can. 25 Hz is past what a wideband can resolve; the faster values
are for transients, and cost disk — at 100 Hz a rusEFI's 823 channels are about
14 MB a minute. A value of zero read from an older file is **not** honoured as
"uncapped"; it takes the default.

**`singleRequestBlock`** is faster where the firmware allows it and fatal where it
does not. See [Live
connection](live-connection.md#reading-the-block-in-one-request).

**`recordOnConnect`** defaults to off. A settings file written before this was a
choice came from a version that always recorded, so absent takes the new default
rather than the old behaviour. That is intended, not an oversight.

**`knownEcus`** is keyed by hardware id rather than COM port, because Windows
hands port numbers out and reuses them. Windows also names the chip rather than
the ECU — a Speeduino shows up as "Arduino Mega 2560" — which is why what answered
is remembered separately. **Tools ▸ Forget remembered ECUs** clears
`knownEcus` and `ecuLastUsed` and nothing else.

**`obd2BatchDeaths`** records adapters that cannot survive a batched request. Two
occurrences before it is believed; after that the adapter is not probed again. A
different adapter starts clean. See [OBD2 ▸ Adapters that cannot survive being
asked](obd2.md#adapters-that-cannot-survive-being-asked).

### Example

```json
{
  "version": 1,
  "themeId": "solarized-dark",
  "dataFolder": "D:\\Tuning\\OpenLogViewer",
  "liveRate": 50,
  "singleRequestBlock": false,
  "recordOnConnect": true,
  "units": "Metric"
}
```

## presets.json

`%APPDATA%\OpenLogViewer\presets.json`

Named sets of plotted channels, held **by channel name**. A preset saved against
one log applies to any other log — or live session — carrying those names.

Managed from the **+ Save** button and the preset chips above the channel list.

## filters.json

`%APPDATA%\OpenLogViewer\filters.json`

Conditions that decide which samples a histogram, scatter or VE analysis counts.
Held by channel name.

A filter naming a channel the open log does not have is **reported and skipped**,
never applied as "reject everything".

Managed from **+ Add filter** in the histogram side panel.

## math.json

`%APPDATA%\OpenLogViewer\math.json`

[Calculated channel](calculated-channels.md) definitions, held by name and
expression. One that does not fit the open log is reported in the sidebar rather
than dropped.

Managed from **ƒ Add calculated channel**.

## channels.json

`%APPDATA%\OpenLogViewer\channels.json`

Per-channel appearance, held by channel name: **pinned colour**, **pinned scale**
and **smoothing level**.

| Held | Values |
| --- | --- |
| Colour | A colour from the scheme's palette, or none |
| Scale | A minimum and a maximum, or none |
| Smoothing | `None`, `Light`, `Medium` or `Strong` — a median of 1, 5, 15 or 51 samples |

A pinned scale is stored **in the log's own units**, so switching between metric
and imperial redraws the labels without moving the range.

Smoothing here affects **drawing only**. Every measurement — insights, VE
calibration, the histogram, the scatter, the statistics and every export — reads
the channel as logged. See [User guide ▸
Smoothing](user-guide.md#smoothing).

> **NOTICE:** The file holds at most **500 channels**. Past that limit a new
> entry is refused and the application says so, rather than quietly evicting one
> you set earlier.

Managed by right-clicking a channel row; **Back to automatic** clears all three
for that channel. Deleting this file returns every channel to automatic colours,
scales and no smoothing.

## ssm-parameters.json

`%USERPROFILE%\OpenLogViewer\ECU definitions\ssm-parameters.json`

This one lives with *your* files, not the application's, because the addresses in
it are yours. Written out with a template the first time it is wanted.

Full field reference: [Subaru SSM ▸ Parameter file
reference](subaru-ssm.md#parameter-file-reference).

## Settings reachable from the interface

Everything above that has a control, in one table.

| Setting | Where | Default |
| --- | --- | ---: |
| Colour scheme | **View ▸ Theme**, or the box at the top right | Midnight |
| Units | **View ▸ Units** | As reported |
| Overlaid / stacked traces | **View**, or the toolbar | Overlaid |
| Draw steady channels as steady | **View** | On |
| Hide unused channels | Channel sidebar | On |
| Histogram columns / rows | Histogram side panel | 16 × 16 |
| Histogram statistic | Histogram side panel | Mean |
| Only the zoomed time range | Histogram side panel | Off |
| Colour by sample count | Histogram side panel | Off |
| VE min samples | Histogram side panel | 12 |
| VE max change % | Histogram side panel | 15 |
| VE wideband delay | Histogram side panel | 0 s |
| Logging rate | **Tools ▸ Logging rate** | 25 Hz |
| Record as soon as I connect | **Tools** | Off |
| Read the block in one request | **Tools** | Off |
| Data folder | **File ▸ Data folder ▸ Change the folder…** | `%USERPROFILE%\OpenLogViewer` |
| Allow an AI agent to connect | **AI agent** | Off, and never remembered |

Only the rows that appear in [settings.json](#settingsjson) persist between
sessions. The rest are per-session.

> **NOTICE:** **The MCP server is off at every launch and never remembers being
> on.** There is deliberately no setting that starts it armed. See [AI agent
> access (MCP)](mcp-server.md).

## Editing the files by hand

All five are plain JSON and safe to edit, copy between machines, or keep in
version control.

**Close the application first.** Files are written on change, and a running
instance will overwrite your edit.

Reads accept any property casing, `//` comments and trailing commas. Enum-valued
settings are the exception: `units` is matched **case-sensitively**, so `metric`
does not work where `Metric` does.

Writes are made through a temporary file and moved into place, so an interrupted
write cannot leave a half-written settings file behind.

**An unreadable file is treated as absent**, not as an error: a corrupted
`presets.json` costs you your presets and nothing else, and the application
starts normally.

## Related

- [User guide](user-guide.md)
- [Command line](command-line.md) — overriding some of these for one run
- [Troubleshooting](troubleshooting.md)
