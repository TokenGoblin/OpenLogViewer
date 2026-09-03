# Plan: MCP control and support for OpenLogViewer

*Written against [`docs/mcp-embedding-guide.md`](mcp-embedding-guide.md), the portable
recipe from OpenEprom. This file is the OpenLogViewer-specific reading of it: what
transfers unchanged, what does not, and what has to be built here that did not exist
there.*

---

## Status

**Phases 0 to 5 are built.** The operator-facing description of the finished thing is
[`docs/mcp-server.md`](mcp-server.md); this file is kept for the reasoning behind it.

| Phase | State |
|---|---|
| 0 — the confirmation refactor | Done. `IWriteConfirmation`, all six gates moved into the view model, `WriteConfirmationTests` |
| 1 — host and toggle | Done. `McpServerHost`, `McpSettingsViewModel`, the AI agent menu, the status-bar and title indicators, disarm on exit |
| 2 — reading | Done. Logs, analysis, live, faults, tune reads, app state |
| 3 — in-memory editing | Done. Table and settings edits, saved tunes, restore planning |
| 4 — writes | Done. The five gated writes and `get_write_readiness` |
| 5 — the operator doc | Done. [`docs/mcp-server.md`](mcp-server.md) |

57 tools. 2,307 tests pass, and the server has been driven end to end against the running
application over a real socket. **The write and burn tools have never met real hardware** —
see §8, which is the part still outstanding.

Two things came out differently from the plan below, both worth knowing:

- **§4's `describe_cell_trace` became `get_histogram_cell`.** `MainViewModel.DescribeCellTrace`
  turned out to be a hint-setter that is handed the visit runs by the histogram view, not
  something that computes them — so the tool returns the cell, its value and its sample
  count rather than a sample-by-sample trace it would have had to invent.
- **The window became an abstraction.** `IWindowSource` rather than `MainWindow`, so the
  server can be stood up and driven end to end in a test with no window at all;
  `screenshot` then refuses, which is the honest answer.

And one thing the plan got right for the wrong reason: §1.3 said the marshalling rule was
not a formality here. The first end-to-end test proved it by failing — an inline dispatcher
ran `open_log` on the web server's thread and WPF refused to let the channel list's
`ICollectionView` be changed from it. The test harness now runs a real dispatcher on a real
STA thread, which is both faithful to the application and the only arrangement that can
prove the rule is being followed.

### What the review found

A `/code-review` pass over the finished work found four things worth recording, because
each is a mistake the tests as written could not have caught.

**`edit_table` refused every `add`.** `TuneEditKind.Add` is the enum's zero value, so the
guard asking "did the operation switch fall through to `default`?" was true for a perfectly
good nudge — the commonest table edit there is, and the one the documentation leads with.
The end-to-end suite calls every tool once with deliberately useless arguments to prove
dependency resolution, so it could not notice a tool refusing a good request. Fixed by
matching on the word and never on the resulting `Kind`, plus `McpToolTests`, which calls
each advertised operation for real.

**The write tools reported failures as successes.** `sent` was derived from whether the
message began with "Nothing", so `"No table is open."` and `"Not connected to an ECU."`
both came back as `sent: true` — on the flag the tool descriptions tell an agent to branch
on. Guessing a machine-readable outcome from prose written for a person was the mistake;
the five write methods now return `WriteResult(Reached, Message)`, with an implicit
conversion from `string` so every existing refusal keeps returning a sentence and means
`Reached: false` by default. That is the safe direction for a path somebody adds later.

**A confirmation dialog pumps the dispatcher.** `MessageBox.Show` runs a modal message
loop, so while a person looked at "Send 3 changed cells to the ECU?", every other call an
agent made was dispatched and ran underneath it — `disconnect`, `revert_table`,
`open_tune_table` — and the write then went ahead against whatever was left. Queueing on
the dispatcher does not fix this because the modal loop *is* the dispatcher. Fixed with
`SerializedUiDispatcher`, which holds a semaphore around the whole call so the other calls
never reach the dispatcher at all. A read now waits behind a write that is waiting for a
person, which is the correct order: until that dialog is answered there is no settled state
to report.

**The connected light could go stale across a re-arm.** One `McpClientActivity` was reused
for the life of the host and its last-request timestamp was never reset, so disarming with
an agent attached and re-arming inside the 45-second idle window announced a client that
was not there — the exact failure the class was written to prevent. Now built fresh per
arm.

Also fixed: `ToggleAsync` caught only two exception types, and anything else escaping into
the `async void` menu handler would have closed the window from a menu click; and
`build_histogram` built over the whole log while the window, with "only the zoomed time
range" ticked, showed the zoomed span — the agent and the person reading different numbers
under one heading. The tool now lets the window's own rebuild answer where there is one,
falls back to the whole log where there is not, and reports which range the grid covers.

---

The goal is the guide's seven properties, not "the app has some tools": an agent acting
on the live window, off by default, loopback only, a visible connection state, every call
marshalled to the UI thread, no safety gate bypassed, and nothing exposed that a person
has to attest to.

---

## 1. What is different about this app

Four things, and each one changes a step of the recipe. They are worth settling before
any code is written, because three of them are decisions rather than typing.

### 1.1 There is no DI container to forward from

The guide's central instruction — `AddSingleton(appServices.GetRequiredService<T>())`,
register the *instance* — assumes the app already has a container. OpenLogViewer does not.
`App.OnStartup` news up `MainWindow` (`App.xaml.cs`), and `MainWindow` news up its view
model on a field initialiser (`MainWindow.xaml.cs:16`, `private readonly MainViewModel _vm = new();`).

**Do not retrofit a container into the app to satisfy the recipe.** The forwarding
requirement is about *identity* — the tools must hold the same objects the window holds —
and that is satisfied by passing the live instances into `ArmAsync` directly:

```csharp
Task ArmAsync(MainWindow window, MainViewModel vm, IUiDispatcher dispatcher, int port);
```

…which then registers them by value in the MCP host's own container. The MCP host still
gets a container, because the SDK resolves tool parameters from one; the application does
not need one, and adding one would be a large unrelated refactor of a 5,800-line view
model for no safety benefit.

The failure mode the guide warns about is unchanged and still the one that wastes a day:
register `AddSingleton<MainViewModel>()` and every tool silently edits a second view model
the window has never heard of. The section 6 test is what catches it.

### 1.2 The confirmations are in the click handlers, not the commands

This is the significant finding, and the plan turns on it.

OpenEprom could satisfy Rule 1 ("tools call the same commands the buttons call") for free,
because its confirmation dialog is inside `BurnViewModel`'s command. Here it is not.
`MainWindow.xaml.cs` holds the gate and the view model holds the action:

| Handler | Gate | Action it calls |
|---|---|---|
| `OnWriteTableClick` (`:449`) | "Send N changed cells… takes effect immediately on a running engine" | `_vm.WriteTableToEcu()` |
| `OnBurnTableClick` (`:475`) | "This is permanent… burn with the engine stopped" | `_vm.BurnTableToEcu()` |
| `OnWriteSettingsClick` (`:502`) | "N settings, N bytes across N pages" | `_vm.WriteSettingsToEcu()` |
| `OnSendCurveClick` (`:534`) | "Send N moved points… takes effect at once" | `_vm.WriteCurveToEcu()` |
| `OnBurnSettingsClick` (`:561`) | "Burn N pages… permanent" | `_vm.BurnSettingsToEcu()` |
| `FaultsPanel.OnClearClick` (`:240`) | clearing the MIL and readiness monitors | `_vm.ClearFaults()` |

A tool that called `_vm.WriteTableToEcu()` would compile, work, and write to a running
engine **with no confirmation at all** — not because anyone bypassed a gate, but because
the gate is not on that path. This is exactly the "second code path with the checks
removed" the guide's Rule 1 exists to prevent, except it would arrive by accident.

**Fix it before writing the first write tool**, by moving the gate to where both callers
meet it:

```csharp
// Injected; the real one shows the MessageBox, tests supply an answer.
public interface IWriteConfirmation
{
    bool Confirm(WriteRequest request);   // what, how many, permanent or not
}
```

`MainViewModel.WriteTableToEcu()` and its siblings ask it; `OnWriteTableClick` becomes a
one-liner that calls the view model. The dialog text moves with the check, unchanged —
it is good text and it is doing the work. After this refactor a tool calling
`WriteTableToEcu()` genuinely blocks on a human answering the dialog in the running app,
which is the correct behaviour and not a bug to route around.

This refactor is a prerequisite, not a nice-to-have, and it is worth landing and testing
on its own before any MCP code exists.

### 1.3 The live session mutates state continuously

OpenEprom's model sat still between commands. This one does not: a live session polls at
up to 200 Hz on a dispatcher timer, appending samples and updating gauges. Two
consequences:

- The guide's "reads are not exempt from marshalling" rule is not a formality here. An
  unmarshalled `list_channels` during a live session reads an `ObservableCollection`
  mid-append and can return a torn view or throw.
- The guide's **check-and-start atomicity** matters more, not less. Decide and start in
  one dispatcher turn; await from the pool.

### 1.4 The app already has an automation surface

`App.xaml.cs` carries around forty command-line switches — `--connect`, `--open-tune`,
`--histogram`, `--plan-restore`, `--screenshot`. That inventory is the best available
specification of "what is worth driving from outside", and the tool list in §4 is largely
it, made interactive. Two things follow:

- **`--plan-restore` exists and there is deliberately no flag that applies one.** The
  comment says why: *"this is the largest change the application can make to an engine,
  and it is not something to fall out of a command line."* That precedent binds MCP —
  see §3.
- The `--screenshot` render-to-PNG path already exists and should become a tool. An agent
  that can see the window it is driving is the cheapest possible version of the guide's
  "make agent actions visible".

---

## 2. Architecture

Unchanged from the guide in shape:

```
MainWindow (owns MainViewModel)        McpServerHost (built per arm)
        |                                      |
        +-- passed by instance --------------->+  WebApplication
        |                                      |  - activity middleware
   UI thread <-- every tool marshals here      |  - MapMcp()
                via IUiDispatcher              |  - tool classes
                                               +-- http://127.0.0.1:7071
```

**Project changes** (`src/OpenLogViewer.App/OpenLogViewer.App.csproj`):

```xml
<ItemGroup>
  <FrameworkReference Include="Microsoft.AspNetCore.App" />
</ItemGroup>
<ItemGroup>
  <PackageReference Include="ModelContextProtocol.AspNetCore" Version="2.2.0" />
</ItemGroup>
```

2.2.0 is already in the local NuGet cache, so this builds offline. The repo pins explicit
versions on every `PackageReference` and has no `Directory.Packages.props` — follow that.
CI builds with warnings as errors and fails on known advisories, so expect to deal with
NU1510 if `Microsoft.Extensions.Hosting` is ever referenced explicitly; the shared
framework already carries it.

**Logging.** The guide routes the MCP host into the app's existing pipeline. This app's
pipeline is `App.Report` — a line per event into a temp file, deliberately, because a WPF
app has no console. So: `builder.Logging.ClearProviders()` plus one small `ILoggerProvider`
forwarding warnings and above to `App.Report`. Not a second log file, and not Serilog:
adding a logging framework to carry six lines a session is not worth the dependency.

**Files** (`src/OpenLogViewer.App/Mcp/`):

```
McpServerHost.cs         IMcpServerHost, arm/disarm, lifecycle lock, activity tracking
McpSettingsViewModel.cs  the toggle, the status line, the connected poll
UiDispatcher.cs          IUiDispatcher and the WPF implementation
LogTools.cs              opening and reading logs
AnalysisTools.cs         histogram, scatter, VE, insights, power
ChannelTools.cs          channels, math channels, filters, presets, styling
LiveTools.cs             connecting, live status, recording
FaultTools.cs            reading fault codes
TuneTools.cs             reading and editing the tune in memory
TuneFileTools.cs         saved tunes, comparison, restore planning
EcuWriteTools.cs         the five gated writes, and nothing else
AppTools.cs              app state, mode, screenshot, exports
```

---

## 3. The safety boundary — written before the first tool

Per the guide's Rule 2, the never-exposed list goes down first, with its reasoning,
because removing a tool later is much harder than never adding it.

### Never exposed, permanently

**`apply_restore`.** `MainViewModel.ApplyRestore()` writes a whole saved tune into a
controller. `--plan-restore` exists and applies nothing, on purpose, and the reasoning
recorded in `App.xaml.cs` applies with more force to an agent than to a command line. MCP
gets `plan_restore` — which returns the plan, the byte and page counts, the shortfall,
and whether the signatures agree — and stops there. A person applies it from the window.

**`clear_faults`.** Clearing DTCs erases freeze-frame data and resets emissions readiness
monitors on someone's car; readiness then takes a drive cycle to rebuild, and a car with
incomplete monitors fails inspection in several jurisdictions. That is an irreversible
change to a physical vehicle's compliance state, made on a diagnosis the agent did not
perform. `scan_faults` yes; clearing stays a button.

**Anything that attests to the engine being stopped.** The burn dialogs say *"Burn with
the engine stopped: the ECU pauses while it writes flash."* This is precisely OpenEprom's
*"nothing in software can see the socket"* — the app cannot verify it and neither can an
agent. There will never be a tool that acknowledges it. The consequence is permanent and
intended: an MCP-triggered burn always requires a person to have answered that dialog.

**Connecting to a port nobody named.** Not a refusal, a default: the connect tools take a
port and never scan-and-attach. A MicroSquirt in a running car and a Speeduino on a bench
look identical over a COM port, and the agent guessing is the wrong party to guess.

### Exposed, but only through the confirmation

`write_table_to_ecu`, `burn_table_to_ecu`, `write_settings_to_ecu`,
`burn_settings_to_ecu`, `write_curve_to_ecu` — each calls the same view-model method the
button calls, *after* §1.2 has put the confirmation inside it. Each tool call blocks until
a person answers. That is the design, not a limitation of it.

### The handoff tool

`get_write_readiness` is this app's `get_preflight_status`, and splits its findings the
same way:

- **`needsAcknowledgement`** — what an agent can still clear itself: no controller
  connected (call `connect_serial`), no table open (`open_tune_table`), nothing changed
  (`edit_table`), the tune is a placeholder or came from a file (`TuneIsPlaceholder` /
  `TuneIsFromFile` — load one off the controller).
- **`remainingForOperator`** — the confirmation dialog, and the engine-stopped
  attestation for a burn.
- **`handoff`** — one sentence saying whose move it is.

The distinction matters for the reason it did in OpenEprom: `CanWriteTable` being false
usually means the agent has work left to do, not that it should give up.

### Still true, and worth asserting in tests

- **In-memory edits stay in memory.** `edit_table` and `set_setting` move `TuneEdit` /
  `TuneSettingsEdit` and nothing else, exactly as the keyboard does. Only the five write
  tools reach a controller.
- **Recordings are files that are only appended to.** No tool deletes one.
- **The About box's claim needs revising.** It reads *"No network code: nothing here is
  ever sent anywhere."* (`MainWindow.xaml.cs:414`). Wi-Fi OBD2 already strained that; a
  listening socket breaks it. Reword to what stays true — nothing leaves the machine, the
  MCP listener is loopback-only and off by default — rather than leave a claim the build
  no longer honours.

---

## 4. Tool inventory

Grouped by file. Tool names are derived from method names by the SDK, so
`GetLogSummary` becomes `get_log_summary`.

### Logs — `LogTools`

| Tool | Notes |
|---|---|
| `open_log` | `MainViewModel.Load(path)`; any format `LogReaderFactory` handles |
| `get_log_summary` | name, sample count, duration, rate, channel count, source |
| `list_channels` | name, units, category, role, plotted, min/max/mean |
| `get_channel_statistics` | one channel, the full `ChannelStatistics` |
| `read_samples` | **capped at 4096 samples per call**, non-overflowing bounds check |
| `find_in_log` | `RunFind` over a `LogSearch` condition; returns the hit spans |
| `step_finding` | forward and back through the hits |
| `set_selection` / `get_selection` | the span the analyses run over |
| `set_cursor` | and returns the row under it |
| `export_log_csv` | path, plotted-only flag |
| `load_comparison` / `clear_comparison` | the second log behind difference traces |
| `get_comparison_summary` | `ChannelOverlap`, and what differs |

### Analysis — `AnalysisTools`

| Tool | Notes |
|---|---|
| `build_histogram` | axes, size, colour-by-count; opens the Histogram view |
| `get_histogram_table` | cells in engineering units, with counts |
| `build_scatter` / `get_scatter_points` | capped |
| `describe_cell_trace` | which samples fed a cell — the "why is this cell that value" tool |
| `run_ve_analysis` | `VeAnalyze`, with minimum samples and maximum change |
| `find_ve_delay` | `FindVeDelay`; returns the finding and the note |
| `get_insights` | `LogInsights` findings |
| `estimate_power` | `EstimatePower(EngineSpec)` |
| `add_power_channels` | derived channels from an estimate |

### Channels — `ChannelTools`

`set_channel_visible`, `set_all_visible`, `plot_common`, `set_channel_style`
(colour, range, units), `set_smoothing`, `clear_style`, `add_math_channel`,
`list_math_channels`, `remove_math_channel`, `add_filter`, `list_filters`,
`delete_filter`, `set_all_filters`, `save_preset`, `list_presets`, `apply_preset`.

### Live — `LiveTools`

| Tool | Notes |
|---|---|
| `list_serial_ports` | with the friendly names `SerialPortNames` resolves |
| `list_ble_adapters` | `BleDevices`; a BLE-only dongle never appears as a COM port |
| `connect_serial` | a named port; MegaSquirt, Speeduino, rusEFI |
| `connect_obd2` / `connect_obd2_wifi` / `connect_obd2_ble` | ELM327 over each transport |
| `connect_ssm` | Subaru |
| `connect_maxxecu` | |
| `disconnect` | |
| `get_live_status` | running, healthy, rate, detail, undecoded PIDs |
| `read_live_channels` | current values, one snapshot |
| `start_recording` / `stop_recording` / `get_recording_status` | |

Every connect tool is check-and-start atomic against `IsLive`, and refuses rather than
silently reconnecting.

### Faults — `FaultTools`

`scan_faults` (codes, descriptions, freeze frame, readiness monitors) and
`get_fault_description`. **No `clear_faults`** — §3.

### Tune, in memory — `TuneTools`

| Tool | Notes |
|---|---|
| `get_tune_summary` | source, detail, warnings, placeholder and from-file flags |
| `list_tune_tables` | every `TuneTable` the layout offers |
| `open_tune_table` | **also opens it on the Calibration tab** — visible, per the guide |
| `get_table_cells` | the full grid in engineering units, with both axes |
| `select_cells` | the selection edits apply to |
| `edit_table` | add / scale / set / interpolate / smooth / revert — the same `TuneTableEdit` the keyboard raises |
| `set_table_cells` | explicit values; **returns changed and clamped counts** |
| `get_table_change_preview` | what would be sent, before sending it |
| `revert_table` | |
| `list_settings_pages` / `open_settings_page` | `SettingsMenu`, `SettingsDialog` |
| `get_settings_fields` / `set_setting` | one constant at a time |
| `revert_settings` | |
| `list_curves` / `set_curve_point` / `revert_curve` | |

Report clamping loudly. The guide's warning applies verbatim here: a clamped cell is a
value you did not get, and on a spark table it can be a value moving the opposite way to
the one intended.

### Tune files — `TuneFileTools`

`open_saved_tune`, `save_tune_to_file`, `compare_with_saved_tune` (returns the
`TuneDifference` list), `plan_restore` (returns the `TuneRestorePlan`: writes, bytes,
pages, missing, rejected, whether the signatures agree, the shortfall).
**No `apply_restore`.**

### Writes to the ECU — `EcuWriteTools`

`write_table_to_ecu`, `burn_table_to_ecu`, `write_settings_to_ecu`,
`burn_settings_to_ecu`, `write_curve_to_ecu`, `get_write_readiness`. Every one calls the
same view-model method the button calls, blocks on the confirmation, and is
check-and-start atomic.

### The app itself — `AppTools`

`get_app_state` (mode, view, theme, workspace, live state, loaded log, loaded tune),
`set_workspace_mode`, `set_view`, `set_theme`, `screenshot` (reuses the `--screenshot`
render path and returns the written path), `export_all`, `get_run_log` (the tail of
`openlogviewer-run.log`, for diagnosing a failure the agent itself caused).

---

## 5. Arming and the indicator

`McpSettingsViewModel` holds `IsArmed`, `IsClientConnected`, `Port`, `StatusLine` and one
`Toggle` command. Off by default; **not written to `SettingsStore`**, and no setting that
would make it start armed.

**Menu.** A new top-level `_AI agent` menu beside `_Tools`, holding one checkable item,
*"Allow an AI agent to connect (MCP)"*, with `StatusLine` as its tooltip. Top-level rather
than buried inside Tools: this is a thing to be able to see and switch off without hunting
for it.

**Indicator.** Two places, because the app has two:

- Appended to `MainViewModel.Title` — the window title is already bound at
  `MainWindow.xaml:5` — so it is visible when the window is not focused.
- Its own `TextBlock` in the status bar row (`MainWindow.xaml:351`), amber when connected,
  so it reads at a glance while working.

Three states, in operator language, exactly as the guide has them:

```
AI agent access: OFF
AI agent access ON, waiting · 127.0.0.1:7071
AI CONNECTED OVER MCP · 127.0.0.1:7071
```

Connection detection needs both signals — an in-flight count for a streaming client, a
45-second idle window for one that posts and hangs up — polled on a 1 s `DispatcherTimer`.

Arm failure (almost always the port already being in use) surfaces as a readable status
line, and the half-started `WebApplication` is disposed rather than left running. Disarm
from `OnExit` unconditionally.

---

## 6. Tests

`tests/OpenLogViewer.App.Tests` already has the fixtures this needs: `ViewModelHarness`
builds a view model over a real synthetic log on disk, and `FakeController`, `FakeElm`,
`FakeElmOverTcp` and `FakeSubaru` stand in for hardware. An end-to-end MCP test therefore
needs no ECU and no adapter.

New harness pieces: `ImmediateUiDispatcher`, `FakeMcpServerHost`, and
`FakeWriteConfirmation` (answers yes or no without a window).

The tests that earn their keep:

1. **The confirmation refactor holds.** For each of the five writes: with the confirmation
   answering *no*, nothing reaches the controller. This is the §1.2 regression test and it
   lands with the refactor, before any MCP code exists.
2. **Arming starts a listener; disarming stops it.**
3. **A real MCP client can list the tools** — proves registration and transport.
4. **A tool call reflects the harness's own live instance.** Open a log through `open_log`,
   then assert against the exact `MainViewModel` the test created. This is the proof it is
   not a second, disconnected view model — the silent failure from §1.1.
5. **Every registered tool resolves its dependencies** — one `[InlineData]` row per tool,
   called once against a minimal fixture, asserting `IsError is not true` (it is a `bool?`,
   and `Assert.False` treats null as a failure). Deliberately invalid arguments are fine;
   a structured refusal is not an error. The guide is right that this is the test that pays
   for the whole file: a service a tool needs but `ArmAsync` forgot to pass fails at *call*
   time, not at compile or arm time.
6. **The real `WpfDispatcher`, once, on its own.**
7. **`0.0.0.0` and `::` appear nowhere** — a grep, as a test.
8. **`apply_restore` and `clear_faults` are not in the tool list.** A test asserting the
   absence of a tool looks odd right up until somebody adds one by reflex, and then it is
   the only thing that catches it.

---

## 7. Order of work

Each phase is testable before the next, and phase 0 is not optional.

**Phase 0 — the confirmation refactor.** `IWriteConfirmation`, the gates moved from the
five click handlers into the view model, test 1. No MCP code at all. It ships and is
useful on its own: it is also what makes the write paths testable in the first place.

**Phase 1 — host and toggle, no tools.** Package references, `IUiDispatcher`,
`McpServerHost`, `McpSettingsViewModel`, the menu item, the two indicators, disarm on
exit. Tests 2, 6, 7. At the end of it the server arms, a client connects and sees an empty
tool list, and the window says so.

**Phase 2 — reading.** `LogTools`, `AnalysisTools`, `ChannelTools`, `FaultTools`
(`scan_faults` only), the read half of `TuneTools`, `AppTools`. Tests 3, 4, 5. This is
already most of the value: an agent that can open a log, build a histogram, run the VE
analysis and read the tune can do real work and cannot change anything.

**Phase 3 — in-memory editing.** The edit half of `TuneTools`, `TuneFileTools`, the
`ChannelTools` writes. Still nothing reaches a controller.

**Phase 4 — writes.** `EcuWriteTools` and `get_write_readiness`, on top of phase 0.
Test 8.

**Phase 5 — the operator doc.** `docs/mcp-server.md` for this app: arming, connecting,
the tool inventory, and a "what is NOT bypassed" section naming the never-exposed list
from §3. OpenEprom's file of that name is the model for it.

---

## 8. Verification on hardware

The tests will pass long before any of this is known to work. As with everything else in
this repo, the tool paths need a real controller behind them at least once:

- **Speeduino on the bench** (COM14) — read, write and burn end to end through MCP.
  Opening the port resets the board, so unburned changes are lost; that is the ECU's
  behaviour and the tools should say so rather than hide it.
- **rusEFI** (uaEFI, COM8) — a second protocol through the same tools.
- **OBD2 over the Wi-Fi and BLE dongles** — `connect_obd2_wifi`, `connect_obd2_ble` and
  `scan_faults` against a running car.
- **The MicroSquirt (COM3) is in a live car** and stays read-only unless explicitly asked.

Until each of those has actually happened, the honest status of the corresponding tools is
"written and tested, never met the hardware."
