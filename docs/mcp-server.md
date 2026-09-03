# AI agent access (MCP)

OpenLogViewer can host a local [Model Context Protocol](https://modelcontextprotocol.io)
server, so an AI agent — Claude, or any other MCP-capable client — can drive the
application you are looking at: open and read datalogs, build histograms and scatter
plots, run the VE analysis, connect to a controller or a vehicle, read fault codes, read
and edit the tune, and send changes to an ECU.

It acts on the **live window**. A table an agent opens opens on the Calibration tab; a
histogram it builds is the one on screen. Nothing happens in a second, invisible copy of
the application.

## Arming

The server is **off by default and never remembers being on** — every launch starts
disarmed. There is no setting that makes it start armed.

1. Open the **AI agent** menu.
2. Click **Allow an AI agent to connect (MCP)**. The item is a checkbox: ticking it arms
   the server, unticking it stops it.
3. The status bar shows the live state:

   | Shown | Means |
   |---|---|
   | *(nothing)* | No listener. Nothing outside the window can act on the application. |
   | `○ AI agent access ON, waiting · 127.0.0.1:7071` | Listening, nothing attached. |
   | `● AI CONNECTED OVER MCP · 127.0.0.1:7071` | An agent is actually talking to it — amber, because this is the state to notice at a glance. |

   The window title carries the same three states, so it is legible when the window is not
   focused. Hover the menu item for the full status line, including the reason if arming
   failed — most often the port is already in use by a second copy of the application.

Armed and connected are deliberately separate. A listener being up is worth seeing on its
own, and saying an AI is connected when none is would make the indicator worth ignoring. A
client counts as attached while it holds a request open — which is what a streaming MCP
client does for as long as it is there — or for 45 seconds after its last call, so a client
that posts and hangs up between calls does not flicker.

Unticking the menu item, or closing the application, stops the listener immediately; any
agent connected at that moment loses its connection.

The server binds **loopback only** (`127.0.0.1`), never a network-visible address. Nothing
off this machine can reach it, armed or not — there is a test that greps the source to keep
it that way.

**From the command line.** `OpenLogViewer.App.exe --mcp` arms it at startup. That is not a
setting and not persistence: it is typed afresh every launch, which is the same act as
ticking the menu item.

## Connecting a client

The server speaks MCP over HTTP at `http://127.0.0.1:7071/`.

**Claude Code:**

```sh
claude mcp add --transport http openlogviewer http://127.0.0.1:7071/
```

**Claude Desktop** (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "openlogviewer": {
      "url": "http://127.0.0.1:7071/"
    }
  }
}
```

Either way the application must already be running and armed before the client connects;
there is nothing to launch on the agent's behalf. A client reporting `ConnectionRefused` is
this design working, not a misconfiguration.

## Tool inventory

64 tools. Every one returns JSON with a boolean outcome field, and a `reason` when the
answer is no — so a precondition the window has not met yet ("no log is open") comes back
as something an agent can act on rather than as an opaque error.

### The application

| Tool | What it does |
|---|---|
| `get_app_state` | Mode, view, theme, workspace, the loaded log, the loaded tune, whether anything is connected. The one to call first |
| `set_workspace_mode` | Log, Gauges, Calibration or Guide |
| `set_view` | Plot, Histogram or Scatter |
| `screenshot` | Draws the window to a PNG — the cheapest way for an agent to see what it did |
| `get_run_log` | The tail of the application's run log, for diagnosing a failure it caused |

### Logs

| Tool | What it does |
|---|---|
| `open_log` | MegaSquirt/TunerStudio (.msl, .csv), MLG binary, MaxxECU, delimited text |
| `get_log_summary` | Format, samples, duration, rate, when recorded, embedded tune |
| `list_channels` | Every channel with units, category, range, whether plotted, whether flat |
| `get_channel_statistics` | Min, max, mean and count — over the selection if there is one |
| `read_samples` | Raw samples, **capped at 4096 per call**, with a `stride` for longer spans |
| `find_in_log` | `"RPM > 4000 and CLT > 100"` — opens the find bar and returns the hit runs |
| `set_selection` | The sample range the analyses run over |
| `export_log_csv` | Writes the log out, no dialog |
| `load_comparison` | A second log, for difference traces |

### Analysis

| Tool | What it does |
|---|---|
| `build_histogram` | Sets the axes and builds it; returns every cell with its visit count |
| `get_histogram_table` | Re-reads it later |
| `get_histogram_cell` | One cell in detail — the sample count is the part the grid cannot show |
| `build_scatter` | The scatter plot |
| `run_ve_analysis` | What the fuel table would have to become for the log's AFR to have hit target |
| `find_ve_delay` | The lag between a fuelling change and the wideband seeing it |
| `get_insights` | The findings, each with the arithmetic behind it |

### Live

`list_serial_ports`, `list_ble_adapters`, `connect_serial`, `connect_obd2`,
`connect_obd2_wifi`, `connect_obd2_ble`, `connect_ssm`, `connect_maxx_ecu`, `disconnect`,
`get_live_status`, `read_live_channels`, `start_recording`, `stop_recording`.

**No tool scans for something to attach to and attaches to it.** Every connect tool takes a
port or an address, because a MicroSquirt in a running car and a Speeduino on a bench look
identical over a COM port, and choosing between them is not a decision to make on somebody's
behalf.

### Faults

`scan_faults` — stored, pending and permanent codes, the warning light, the protocol, and
whether the car's own count disagrees with the codes it listed.

**There is no `clear_faults`.** See below.

### The tune, in memory

| Tool | What it does |
|---|---|
| `get_tune_summary` | What is loaded and whether it may be written back |
| `list_tune_tables` | Every table the definition declares |
| `open_tune_table` | Opens it **on the Calibration tab** and returns every cell in engineering units |
| `get_table_cells` | Re-reads the open table |
| `select_cells` | The cells edits act on |
| `edit_table` | set / add / scale / interpolate / revert — the same operation the keyboard raises |
| `revert_table` | Back to what the controller holds |
| `list_settings_pages`, `open_settings_page`, `set_setting`, `revert_settings` | The settings dialogs |
| `list_curves`, `set_curve_point`, `revert_curve` | Fuelling or timing against a temperature or a voltage. A curve page has no fields — its points are the editable thing, and `open_settings_page` returns them |

`edit_table` returns a **clamped** count as well as a moved count. A clamped cell is a value
you did not get, and on an ignition table it can be a value moving the opposite way to the
one intended.

Nothing here reaches a controller.

### Overview

`push_overview_report`, `get_overview_report`, `get_overview_selections`, `clear_overview`.

A place for an agent to publish a diagnosis rather than leave it in the chat: a headline, a
summary, and a list of findings, each optionally carrying a proposed change — a table cell
or a setting. `push_overview_report` opens the **Overview** window, so a diagnosis never
happens invisibly, and every finding with a change shows a checkbox.

**Nothing here reaches the tune.** This is a report/selection layer, not a sixth way to edit
one. The loop:

1. Read the tune and log with the tools above, then call `push_overview_report` with what
   was found.
2. A person ticks the changes they want, in the window.
3. Call `get_overview_selections` to see exactly what was ticked, with each one's change.
4. Apply each with the tools that already exist — `open_tune_table`/`select_cells`/
   `edit_table` for a cell, `open_settings_page`/`set_setting` for a setting.
5. Call `push_overview_report` again with the next revision.

`push_overview_report` replaces the report outright rather than merging into it — a resolved
finding left on screen would be worse than one dropped. `clear_overview` discards it without
publishing a new one, the same shape as `cancel_restore`.

### Saved tunes

`open_saved_tune`, `save_tune_to_file`, `compare_with_saved_tune`, `plan_restore`,
`cancel_restore`.

`plan_restore` says what restoring a saved tune **would** change — the writes, the bytes,
the pages, what the file asks for that this firmware does not have, whether the signatures
agree — and changes nothing.

### Writing to a controller

`write_table_to_ecu`, `burn_table_to_ecu`, `write_settings_to_ecu`, `burn_settings_to_ecu`,
`write_curve_to_ecu`, and `get_write_readiness`.

`get_write_readiness` splits what is in the way by who can act on it:

- **`needsAcknowledgement`** — work an agent may still be able to finish itself. "No
  controller is connected" is cleared by connecting, not by a tick.
- **`remainingForOperator`** — what only a person at the window can settle.
- **`handoff`** — one line saying whose move it is.

## What is NOT bypassed

Arming the server does not weaken, skip, or route around any of the application's safety
mechanisms.

- **Every write and burn is confirmed by a person, in the running application.** The five
  write tools call the identical view-model method the buttons call, which asks for
  confirmation immediately before the first byte goes out. A write triggered over MCP
  genuinely waits for somebody to answer that dialog before the tool call returns. That is
  correct behaviour, not a bug to work around.

  This is also the one thing that had to be *built* rather than preserved: the
  confirmations used to live in the click handlers, so every other way into the same method
  — a scripted run, and MCP would have been next — reached a running engine with nothing
  asked. Moving the gate into the view model is what makes it hold for all of them.

- **The engine-stopped question is never exposed.** The burn dialogs ask for the engine to
  be stopped, because the controller pauses while it writes flash. Nothing in software can
  see whether an engine is running, and neither can an agent, so **no tool acknowledges it
  and none ever will.** A burn over MCP therefore always requires a person to have answered
  that question.

- **There is no `apply_restore`.** Restoring a saved tune is the largest change this
  application can make to an engine. The command line makes the same call — `--plan-restore`
  exists and applies nothing — and an agent is further from the engine than a script is, not
  closer. Applying one stays a person's move, from Tools ▸ Restore a saved tune to the ECU.

- **There is no `clear_faults`.** Erasing DTCs takes the freeze frame with it — the record
  of what the engine was doing when the fault occurred, and the most useful thing there is
  for an intermittent — and resets the readiness monitors, which the car has to re-earn over
  a full drive cycle before it can pass an emissions test. That is an irreversible change to
  a vehicle's compliance state, made on a diagnosis the agent did not perform. Reading codes
  is a tool; clearing them is a button.

- **One tool call touches the application at a time.** A confirmation dialog is a modal
  message loop, and a modal message loop keeps pumping the window's dispatcher — so without
  this, every other call an agent made while somebody stood looking at "Send 3 changed
  cells to the ECU?" would run underneath the open dialog. It could disconnect the
  controller or revert the table, and the write would then go ahead against whatever was
  left, sending bytes that no longer matched the count in the question that was answered.
  Calls are serialised instead, so a read waits behind a write that is waiting for a
  person. That is the right order: until the dialog is answered there is no settled state
  to report.

- **In-memory edits stay in memory.** `edit_table` and `set_setting` move the same edit
  buffers the keyboard moves and nothing else. Only the five write tools reach a controller.

- **Nothing leaves this machine.** The listener is loopback-only and off by default.

## What has been proven, and what has not

The server, the tools and the confirmation gate are covered by tests, including a real MCP
client over a real socket asserting against the same view model the window is bound to.

The tools have been driven end to end against a **running application**: opening a log,
building a histogram, reading write-readiness and capturing the window all work over MCP,
and the connected indicator lights amber while it happens.

### Verified on a Speeduino, 2026-09-03

`connect_serial`, the tune reads and `write_table_to_ecu` have been run against a real
Speeduino 202501 on COM14. It connected at 10 Hz over 81 channels, read 20 tables and 60
settings pages, opened the VE table with its real breakpoints, and sent one cell: 33 → 34,
read back off the controller, with the pending count dropping to zero. Nothing was burned.

**The confirmation held, and can be timed.** The tool call sat for 64 seconds — the whole
time the dialog was open — and returned only once a person had answered it. That is the
gate doing exactly what it is for.

Since then the settings pages, recording, the analyses over live samples, the saved-tune
tools and the curves have all been driven against the same board: a setting changed and
reverted, four seconds captured to a file, the tune saved to a 73 KB `.msq` that then
compared back **identical, setting for setting**, `plan_restore` against it correctly
finding nothing to do, and a curve point moved and put back.

Four defects turned up that the test suite could not have found, all now fixed:

- A session connected through MCP never produced a sample. The timer that drives a live
  session belongs to the window, not the view model, so calling the view model directly
  opened the port and read the tune and then sat there — nothing on screen moved and
  `list_channels` reported no log. The live tools now go in the way the window and the
  command line do.
- `read_live_channels` returned an em dash for every channel, because it read the value
  under the plot's cursor and a live session nobody is hovering has no cursor.
- `get_tune_summary` reported `source: "none"` for a perfectly good live tune: that field
  named the `.msq` opened to give a log real table axes, which is a different thing.
- `write_curve_to_ecu` existed with no way to reach it. Curve pages were listed and then
  refused on opening — "no fields this can show" — because a curve has points rather than
  fields, so an agent could never legitimately have used the write tool at the end of it.

### Verified against a running application, 2026-09-03

`push_overview_report` was called from a real MCP client, over a real socket, against a
running instance of the application: the Overview window opened on its own, `screenshot`
showed the headline, summary, revision and both findings rendered correctly — badge, accent
colour and the checkbox on the finding carrying a change — and `get_overview_report` /
`get_overview_selections` read back exactly what was published, against the same view model.

### Still to be proven

- **Burning through MCP.** `burn_table_to_ecu` and `burn_settings_to_ecu` are written and
  tested and have never committed a page. On this board the burn command is per-page and
  opening the port resets it, so anything written and not burned is gone at the next
  connection.
- **`write_settings_to_ecu` and `write_curve_to_ecu`** — the table path is proven and both
  of these can now be reached and staged, but neither has been sent.
- **rusEFI** — a second protocol through the same tools.
- **OBD2 over the Wi-Fi and BLE dongles** — `connect_obd2_wifi`, `connect_obd2_ble` and
  `scan_faults` against a running car.
- **The MicroSquirt is in a live car** and stays read-only unless somebody asks otherwise.
- **A person actually ticking a box in the Overview window** and an agent seeing it in
  `get_overview_selections`. The round trip up to that point is proven — see below — but
  nothing has driven the checkbox itself, since that is a person's click, not a tool.
