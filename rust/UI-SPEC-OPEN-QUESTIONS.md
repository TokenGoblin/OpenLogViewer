# UI-SPEC: decisions only the user can make

Collected from `UI-SPEC.md` (2026-11-08). Each item names the section that holds the detail and the C#
behaviour. **Until a question is answered, the builders build what the C# does** (PLAN.md: the C# is right
unless a person decides otherwise). Questions a builder may settle alone stay inline in `UI-SPEC.md` and are
not repeated here.

Answer inline (e.g. "A: keep C#" / "A: fix as recommended") and the orchestrator will fold the answers into the
spec before Wave 7 starts.

## Answered by the user, 2026-11-08

- **0.1 Bug policy: fix all the bugs.** Every behaviour this spec or `CSHARP-DEFECTS.md` marks as a bug is fixed in the Rust, not ported. Each fix gets a test that fails on the C#'s behaviour, and each is listed in `CSHARP-DEFECTS.md` as "fixed in Rust". The C# reference stays the rule for everything that isn't a listed bug.
- **1.1 / 1.2 Lost changes: confirm, naming the pages.** A Yes/No dialog listing the unburned pages and unsent edits before window close, ECU switch, reconnect (including Ctrl+K while live), opening another table, saved tune or definition. Cancel keeps everything.
- **1.6 / 1.7 Agent writes: clear arming on connect, send only agent bytes.** Every connect and reconnect disarms the agent. An agent write sends only its own change, and is refused while the human has unsent edits on the same page.
- **1.4 RPM: warn above idle, allow.** Human Send, Burn and Restore confirmations say prominently when the engine is above about 1,200 rpm; the user can still proceed. The agent's hard gate stays.
- Everything else below: fix as recommended, per 0.1. Where the spec gives no recommendation, the builder proposes one in its report.

## 0. Policy

0.1 **Port C# bugs, or fix them?** About 60 behaviours in the spec look like bugs (stale state, lost edits,
silent failures). Default: port each one as-is and list it in the builder's report, so fixes are separate,
reviewed changes. Alternative: fix the ones marked "recommended" below during the port. Which?

0.2 **`fix-burn-after-reopen` is not merged into the C# yet.** The spec tells the builders to implement its
behaviour (lost-byte tracking, Send re-sends, Burn refusal, Disconnect warning), so the Rust is ahead of
shipping C# there. Confirm.

## 1. Safety: writes, burns, connects (§8 unless stated)

1.1 **Warn before losing unburned or unsent changes.** Nothing warns on window close, on switching ECU, on Ctrl+K
while live, or on a reconnect; the fix branch only puts a status-bar line on Disconnect, which a connect or fault
overwrites at once (§8 Q5). Recommended: a Yes/No confirmation naming the unburned pages before close, switch
and reconnect.

1.2 **Silent discard of unsent edits** when selecting another table, opening a saved tune or opening a
definition (§8 Q12, §4 Q10). Ask first?

1.3 **Table Burn and Restore are enabled with nothing sent** (`CanBurn` only needs a connection), so Burn can
spend a flash erase on an unchanged page; the Burn dialog never mentions unsent on-screen edits (§8 Q4, §4 Q7).
Require something sent?

1.4 **No RPM gate on human writes and burns**; only agents have one (≤ 1200 rpm). Burn/restore dialogs only
advise "engine stopped" (§8 Q16). Add a gate, a warning, or leave as is?

1.5 **Writes stay enabled while the session is reconnecting**, competing with recovery for the port; on a
Speeduino the write lands just before, or is lost to, a reset (§8 Q22). Disable writes while reconnecting?

1.6 **Agent writes flush the human's pending edits** (all unsent settings; unsent cells in the same table) with no
dialog (§8 Q18). Send only the agent's own bytes, or refuse while the human has pending edits?

1.7 **Agent arming survives a connect**: arm while disconnected, then connect to a car, and it is armed there;
it also survives a live reconnect (§8 Q6, §7 Q10). Clear arming on every connect, or allow arming only while
connected?

1.8 **MaxxECU over USB**: the human Send buttons are disabled for it (gate needs a TunerStudio connection) though
the agent can write, and its writes are permanent at once; it is also reachable only from the command line
(§8 Q1, §4 Q5, §9). Enable the human Send path (with its own wording that there is no burn and no undo) and add
a menu entry?

1.9 **Non-finite table values become 0 before the range clamp**, and `NaN`/`Infinity` typed in the Set box may
parse (§8 Q15). Refuse non-finite input? (Recommended.)

1.10 **Send confirmations when only lost bytes are being re-sent** (fix branch) say "Send 0 changed …", and the lit
Send buttons carry no explanation (§8 Q14). Wording for both?

1.11 **Fault-code erase uses the last scan's count** without checking the scan is current, and the Calibration-tab
fault panel scans only once per run, so after connecting to another car it shows (and offers to erase) the
previous car's codes (§5 Q6, Q7). Rescan on every OBD2 connect and before erase?

1.12 **OBD2, SSM and MaxxECU-Bluetooth connects leave the previous ECU's tune on screen** (§8 Q3). Clear it?

1.13 **Ctrl+K reconnects even while live** (disconnect + reconnect, resetting a Speeduino) (§9 Q7, §8). Make it do
nothing while live? (Recommended.)

## 2. Interface behaviour (not safety)

2.1 **Tune-table keys**: `+`/`−` nudge by the firmware step, not by the "by" box the buttons use; on a main
keyboard `+` needs Shift and so always nudges ×10 (§4 Q1, Q2). Intended?

2.2 **Tune-table features the C# lacks**: live operating-point marker and trail; typing a value into a cell,
Enter/Tab, Delete, Ctrl+C/V, undo (§4 Q3, Q4). Add any in the port, or match the C#?

2.3 A table opened from a settings-menu entry has no tools and no Send/Burn, and its edits are lost on leaving
(§4 Q9); table sends do not mark the page for the settings Burn (§4 Q8); changed drop-downs are not outlined and
changed text boxes use `Accent` where cells use `Marker` (§4 Q11); settings-menu entry conditions are never
evaluated, so every page is listed (§4 Q12). Keep or change each?

2.4 **Log-mode controls outside Log mode**: View ▸ Overlaid/Stacked/Find/Reset zoom and the toolbar
Overlaid/Stacked chips show and work in Gauges/Calibration (§9 Q4–Q6). Hide them outside Log mode?

2.5 **Title keeps `Live: …` after Disconnect**, and the toolbar status keeps `Live — …` (§9 Q1, §8 Q10).

2.6 **Menu shortcut text is never drawn** (Ctrl+O/F/K are declared but the menu template has no gesture column)
(§9 Q3). Show shortcuts in menus?

2.7 **Live sessions and an open log**: connecting with a log open keeps the old file's rows and spans; opening a
log while live is overwritten by the next live tick; calculated channels are not built for live sessions
(§2 Q4, Q5; §7 Q12). Treat a new live session like opening a file, and refuse/pause opening a log while live?

2.8 **Plot follow**: jump-to-max/min and find steps are undone by live follow on the next tick; double-click and
View ▸ Reset zoom do opposite things to follow (§2 Q2, Q3). Should jumps stop follow?

2.9 Hover card shows the smoothed value while the row shows the as-logged one (§2 Q7). Keep?

2.10 **Calculated-channel editor loses work**: Add with a blank name/expression closes silently, and when editing,
the original definition is lost; opening a second edit discards the first (§2 Q11, Q12). Fix?
(Recommended.) Deleting presets/channels has no confirmation (§2 Q17).

2.11 **Histogram/scatter**: trace-back under "Only the zoomed time range" rebuilds from the trace's zoom (§3 Q5);
"Show the new numbers" with no tune shows `No samples in this range` (§3 Q8); the difference table uses the
sequential scale with no `+` sign (§3 Q11); a failed comparison is silent because `CompareSummary` is never
shown (§3 Q12).

2.12 **CSV exports write a UTF-8 BOM** though a comment says "No BOM" (§3 Q14, §7 Q14). Keep the BOM?

2.13 **Gauges**: a tune change re-seeds gauges and loses peaks/lows (§5 Q2); the chosen dashboard is not saved
across restarts (§5 Q8). Keep?

2.14 **Calculators**: values read from other pages go stale until the page itself changes (§6 Q1; recommended:
recalculate all); the power window never refreshes when the log changes (§6 Q2); the Injectors page does not
carry BSFC across a fuel change while Pump/Turbo do (§6 Q7); the "Per mile" fallback reads `mpg, $gallon`
(§6 Q10).

2.15 **Closing the window** has no prompt for a live session or a recording in progress (§9 Q11; see also 1.1).

2.16 **Insights `Copy all`** reports success/failure only to the run log (§9 Q9). Show it?

2.17 **Command-line parsing bugs**: `--top-menu`/`--calculators` read their output path as a log;
`--insights`, `--probe-sniper`, `--guide`, `--tuning-project`, `--agent-api` swallow a following log path
(§9 Q10, §7 Q18). Fix in the Rust (the oracle capture script already works around them)?

## 3. Wording

3.1 The agent refusal says `Tick "Allow agent writes"`, the control says `Allow AI writes`, the menu says
`_Agent API…` (with an ellipsis though it opens no dialog), the toolbar says `AI API: On` (§7 Q5–Q7, §8 Q17).
Pick one vocabulary.

3.2 The agent write light turns `Marker` when a write request arrives even if it is refused (§7 Q8). Should it
mean "attempted" or "landed"?

3.3 The "Record as soon as I connect" tooltip says `On by default.` but the default is off (§7 Q13, §8 Q19).

3.4 `{n} page{s} were burned…` reads "1 page were burned" (§8 Q11); the Restore dialog's literal `page(s)` (§4 Q22);
the tune-table hint says "Esc undoes the selection" when Esc reverts the selected cells (§4 Q21). Fix the
grammar or keep the exact text?

## 4. Features and platforms

4.1 **The AI "Overview" feature is not on `retro-theme`.** It exists only on `virtual-dyno` /
`wifi-obd2-and-batching`, on a different MCP architecture with different tool names (§7 2.7, Q1). Include it in
the Rust interface (from which branch), or leave it out until it is merged?

4.2 **Fonts off Windows**: Segoe UI and Consolas do not exist on Linux/macOS. Bundle open fonts (which?) on all
three platforms for identical metrics, or use each OS's own UI and monospace fonts (§1)?

4.3 **Retro text size**: the C# draws the 8×16 pixel font, unsmoothed, at the interface's ordinary sizes
(11–19 px), so some glyphs are uneven; egui smooths glyphs by default. Snap Retro text to 16 px multiples (crisp,
changes layout), or follow the C# sizes with unsmoothed rasterising (§1)?

4.4 **Corners in Retro**: the plot readout card, its hovered row and the gauge face keep fixed rounded corners in
the C# even when the theme squares everything else (`CornerScale` 0). Scale them too? (§1)

4.5 **Title bar off Windows**: keep native decorations (colour not themable), or draw a custom title bar (§1)?

4.6 **Tune-table reference screenshots** need a connected ECU (the C# shows tables only from a controller).
May the orchestrator capture them on the bench Speeduino (COM14; opening the port resets it, nothing is
written)? (§0)
