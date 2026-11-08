# OpenLogViewer interface specification (Rust/egui port)

Package `w6-spec`, written 2026-11-08 from the C# application on branch **`retro-theme`** (commit `0a96f00`), plus
one behaviour from branch **`fix-burn-after-reopen`** (commits `aecc523`, `3c1896d`), marked where it appears.

This document is the only description of the interface the Wave 7–9 builders get. They never see the WPF code.
Where this spec and the C# disagree, the C# is right unless the user decides otherwise (PLAN.md), and the
disagreement is a defect in this spec: report it to the orchestrator.

Decisions only the user can make are collected in **`UI-SPEC-OPEN-QUESTIONS.md`**. Inline
"**Open question:**" notes that are not in that file are ones a builder may settle. Until the user answers,
**build what the C# does**, including where it looks like a bug, and say so in your report.

## How to read this spec

- **Organised by the package that builds each part**: §1 `w6-ui-base`, §2 `w7-plot`, §3 `w7-analysis-views`,
  §4 `w7-calibration`, §5 `w7-gauges-faults`, §6 `w7-calculators`, §7 `w8-viewmodel` (application state, agent
  bridge, projects), §8 `w8-viewmodel` safety (every connect/write/burn/restore path and guard), §9 `w9-shell`.
  §0 below is the index of safety rules and of reference screenshots.
- **Sizes** are in design pixels at 100% zoom (WPF device-independent pixels = egui points at zoom 1). The
  interface zoom multiplies all of them (§1, Interface zoom).
- **Colours** are named by theme token (`Text`, `Accent`, `PanelAlt`…), defined in §1. Never hard-code a colour a
  token provides. A literal hex in this spec means the C# uses that literal.
- **Exact text** in `code spans` or blocks must be reproduced character for character, including the
  typographic quotes, dashes (– —), ellipses (…), the minus sign (−, U+2212) and the middle dot (·). `\n` is a
  line break. `{name}` is a value filled in; `{x:F1}`-style suffixes are .NET format strings, to be produced with
  `olv_core::fmt` (CONVENTIONS.md), in **en-US** unless the spec says invariant. `{s}` is the plural suffix:
  empty when the count is 1, otherwise `s`.
- **Message boxes**: the C# uses the system message box with caption `OpenLogViewer` unless stated. "OK/Cancel,
  default Cancel" means Enter/the focused button is Cancel and Esc cancels. In egui these become modal windows
  with the same caption, text, buttons and default. A confirmation that guards an engine write must be modal:
  nothing else in the window may be clicked while it is open.
- **"Enabled when"** rules are evaluated every frame. A disabled control is drawn per §1 (40% opacity for
  buttons; muted text for menu items) and ignores input.
- **Platform words**: "COM port" means a serial port name on any OS (`/dev/ttyUSB0`, `/dev/cu.usbserial-…`).
  Folder paths written with `%APPDATA%` mean the per-OS configuration folder (CONVENTIONS.md).

## Packages and C# files

| Package | Module | C# files | Section |
|---|---|---|---|
| `w6-ui-base` | `olv::ui` | Theme, ThemeCatalog, ThemeManager, ColorMath, TitleBar, Mvvm, WorkspaceMode, ImageExport, **ThemeFonts, InterfaceScale** (new on `retro-theme`) | §1 |
| `w7-plot` | `olv::plot` | LogPlot, ChannelItem, FilterItem | §2 |
| `w7-analysis-views` | `olv::analysis_views` | HistogramView, ScatterView | §3 |
| `w7-calibration` | `olv::calibration` | TuneTableView, TuneTableEdit, CurveView, SettingRow, SettingsDialog, SettingsMenuEntry, SavedTuneCommands | §4 |
| `w7-gauges-faults` | `olv::gauges` | GaugeView, GaugeItem, FaultsPanel, FaultsWindow | §5 |
| `w7-calculators` | `olv::calculators` | CalculatorsWindow, PowerWindow | §6 |
| `w8-viewmodel` | `olv::state` | MainViewModel, AgentBridge, AgentCommands, ProjectWindow, ProjectCommands | §7, §8 |
| `w9-shell` | `olv::shell` | App, MainWindow, InsightsWindow, AssemblyInfo, **ToolbarFlowPanel** (new on `retro-theme`) | §9 |

`packages.toml` predates `retro-theme`: it does not list ThemeFonts, InterfaceScale or ToolbarFlowPanel. This spec
assigns them as above; the orchestrator should add them to `packages.toml` so `cargo xtask check` stays true.

Most screens live inside MainWindow.xaml in the C#; this spec gives each part to the package that owns its
behaviour, and §9 says where each part sits in the window.


## §0 Safety index and reference screenshots

### Safety index

Every path by which the interface can change an engine or vehicle, with where it is specified. §8 holds the
full table (trigger, enabled rule, exact confirmation, every refusal and result text, bytes written) and is the
checklist the Rust must pass. A test per row must prove that no interface path writes, burns, restores or erases
without the confirmation listed (w3-connection's note to w8-viewmodel).

| # | What changes the engine/vehicle | Trigger(s) | Enabled when | Confirmation | Spec |
|---|---|---|---|---|---|
| 1 | Send table cells to ECU RAM | Calibration ▸ Tables ▸ `Send to ECU` | `CanWriteTable` | OK/Cancel, default Cancel: `Send {n} changed cell{s} of {table} to the ECU?…` | §8 #1, §4 |
| 2 | Burn the table's page | Tables ▸ `Burn` | `CanBurn` (true even with nothing sent) | `Burn the page holding {table} to the ECU's flash?…` | §8 #3 |
| 3 | Send settings | Settings ▸ `Send to ECU` | `CanWriteSettings` | `Send {n} changed setting{s} to the ECU?\n\n{bytes:N0} bytes across {pages} page{s}.…` | §8 #4 |
| 4 | Burn settings / curves | Settings ▸ `Burn`; curve panel `Burn` | `CanBurnSettings` (pages written since read) | `Burn {pages} page{s} of settings to the ECU's flash?…` | §8 #6 |
| 5 | Send curve | `Send curve to ECU` | `CanWriteCurve` | `Send {moved} moved point{s}{ of "title"} to the ECU?…` | §8 #7 |
| 6 | Restore a saved tune (writes differing bytes, never burns) | Tools ▸ `Restore a saved tune to the ECU…` | `CanBurn` | Plan first, then OK/Cancel listing ≤ 12 differences; `THIS FILE IS FOR ANOTHER FIRMWARE.` on signature mismatch | §8 #8 |
| 7 | MaxxECU USB writes (permanent at once, no burn) | same Send buttons (disabled on main: open question) and agent | – | as 1/3 | §8 #2, #5 |
| 8 | Erase OBD2 fault codes (mode 04) | fault panel `Erase codes…` | last scan found stored + pending > 0 | OK/Cancel, default Cancel: `Erase {count} fault code{s} from the vehicle?…` | §5, §8 #9 |
| 9 | Connect (opening a port resets a Speeduino; unburned RAM changes are lost) | Connect menu, Reconnect, Ctrl+K, command line | always (see §8) | **none**; any connect first disconnects | §8 #10, §9 |
| 10 | Disconnect / close window | Tools ▸ Disconnect, toolbar, closing | `IsLive` | **none** (fix branch: Hint warning about unburned pages) | §8 #11, §8.3 |
| 11 | Paired-device scan (opens every paired BT serial port) | Connect menu `Check which of these are switched on` | always | none | §8 #12 |
| 12 | Agent writes (settings, cells, batches; never burns) | agent API | API running **and** `Allow AI writes` armed, plus gates | none: arming is the consent | §7, §8 #14, §8.4.6 |
| 13 | Re-send of bytes lost to a reopen **(fix-burn-after-reopen)** | any Send press | Send buttons stay lit while `HasLostWrites` | as the Send pressed | §8.3 |

Guards that must exist in the Rust exactly as in the C#:

- Placeholder (definition-only) and from-file tunes can never be sent, burned, restored or written by an agent
  (§8.4.1). The Rust should put the check in each write method as well as the button gate (§8 Q4).
- Every page-protocol write is read back and compared; a burn is sent once, never retried, waits up to 8 s
  (§8.4.3).
- **(fix-burn-after-reopen, not yet on main)** A burn of a page whose written bytes were lost to a port reopen is
  refused before anything is sent: `Nothing was burned: the changes written to page {p} were lost when the
  connection was reopened. Send to ECU again, then burn.` Lost bytes are tracked per byte range; every Send
  re-sends them first from the app's tune image; nothing is re-sent without a Send press; Disconnect warns
  `Disconnected with changes that were written but never burned (page {list}). …` (§8.3, §8.1.4).
- Settings fields refuse out-of-range values (except the value first read); table edits clamp (§8.4.4).
- Agent gates, in order: rationale, armed, connected, real tune, RPM ≤ 1200, ≤ 10 writes / 5 s, then
  `confirmDangerous` for limiter/launch/boost/cut constants and ≤ half the declared range per write (§8.4.6).
  Arming is never stored, is off at start-up, and no command-line switch arms it.
- No command-line switch writes, burns or restores: `--tune-cell` edits only the on-screen copy;
  `--plan-restore` only plans (§9).
- Read-only analysis features have **no path into a tune**: VE Calibration (§3), the calculators (§6), the
  power estimate (§6). Do not add one without a separate design.
- There is no read-only mode in the application; keeping the in-car MicroSquirt read-only is an operator
  convention the code does not enforce (§8.4.8).

### Reference screenshots

Captured from the C# (`retro-theme`, built into `tests/screens-build/`) with its own capture switches, using a
real MS3 log (`2026-10-06_20.20.07.mlg`, 182 channels) and tune (`Tyler_V11.msq`). They live in
`rust/oracle/screens/`, named `<mode>-<theme>[-<zoom>].png`. **They are not in git** (a `.gitignore` there
excludes `*.png`) because they show personal logs and tunes; regenerate them with
`rust/oracle/screens/capture.sh`. The window is drawn at the C#'s default size (800×475 design pixels, at
2× device scale), which also shows the toolbar wrapping onto a second row.

| Shot | Themes / zoom | What it shows |
|---|---|---|
| `log-*` | midnight, daylight, retro, retro-150 | Log mode, Plot view, overlaid, default channels |
| `log-pointer-midnight` | | hover readout at the plot centre (`--pointer 0.5,0.5`) |
| `log-find-midnight` | | find bar with `RPM > 4000` |
| `log-stacked-midnight` | | stacked lanes |
| `histogram-*` | midnight, daylight, retro, retro-150 | heat table, RPM × MAP, mean AFR |
| `histogram-ve-midnight` | | VE Calibration with new numbers |
| `scatter-*` | midnight, daylight, retro, retro-150 | scatter |
| `gauges-*` | midnight, daylight, retro, retro-150 | Gauges mode with no connection (empty state) |
| `calibration-settings-*` | midnight, daylight, retro, retro-150 | a saved tune's settings pages (first page) |
| `calibration-noecu-*` | midnight, daylight, retro, retro-150 | Calibration ▸ Tables with no ECU connected (empty state) |
| `guide-*` | midnight, daylight, retro, retro-150 | Guide, "Getting started" |
| `insights-midnight`, `project-midnight`, `power-midnight`, `faults-midnight` | | secondary windows (faults with no OBD2 connection) |
| `calculators-<page>-midnight` | | all 15 calculator pages |
| `menu-file/view/tools/help-midnight`, `menu-view-retro-150`, `menu-connect-midnight` | | drop-down menus; the Retro 150% View menu shows the scroll bar when a menu is taller than the screen |

**Not captured:** the tune table and curves (Calibration ▸ Tables). The C# shows the tables half only with an ECU
connected (a saved tune opens on the settings half, and `--tune-cell` needs a connected tune), so these need a
bench capture (`--connect COM14 --calibration VE --tune-cell 4,4,1 --screenshot …` on the Speeduino) when the
user allows a connection. Until then §4 is the only reference for the heat and grid tune-table looks.

Capture pitfalls found (C# command-line bugs, §9 open questions): the first bare argument is read as the log, so
`--guide`/`--tuning-project` with no value, and the output path of `--calculators`/`--top-menu`, open a `.png` as
a log and hang on a modal error. The script passes the log path first to avoid it. Every run also starts the
agent API (here on port 18765) and rewrites `agent-api.json` in the user's data folder.


## §1 `w6-ui-base`: themes, colours, fonts, zoom, shared widgets

Rust module `olv::ui`. C# files: Theme, ThemeCatalog, ThemeManager, ColorMath, TitleBar, Mvvm,
WorkspaceMode, ImageExport, and, added on `retro-theme` after `packages.toml` was cut, **ThemeFonts and
InterfaceScale** (they belong here: every other package reads them). `ToolbarFlowPanel` goes to w9-shell.

This package owns the look every other package uses: the theme tokens, the colour maths, the fonts, the
corner radii, the interface zoom, the shared widget styles, the title bar colour and PNG export. No screen
may hard-code a colour, a font or a radius that a token below provides.

### Theme model

A theme is a record of **stated** colours plus a **style**. Everything else is **derived** from the stated
colours by the formulas below, so a theme can never be internally inconsistent. Builders must compute the
derived tokens from the formulas (they are normative); the tables of computed values are for checking.

#### Stated tokens

| Token | Meaning / where used |
|---|---|
| `Id` | Stable lowercase id, stored in settings (`ThemeId`) and accepted by `--theme`. Matched case-insensitively. |
| `Name` | Display name in the theme picker. |
| `Group` | `Dark`, `Light`, `Editor`, `HighContrast`, `Retro`. Informational (the picker lists themes in catalog order). |
| `Background` (resource key `Bg`) | Plot and window ground; text-box ground; combo-box closed ground. |
| `Panel` | Sidebars, toolbar, status strip. |
| `PanelAlt` | Raised surfaces: buttons, popups, menus, chips, drop-down lists. |
| `Line` | Borders and separators; title-bar border colour. |
| `Text` | Body text. |
| `Muted` | Secondary text; disabled menu items (they double as captions, so they must read as text). |
| `Accent` | Focus/selection colour: checked toggles, active chips, menu tick, dragged scroll thumb, hovered combo border. |
| `Marker` | Log annotations and the highlight on traced-back samples. |
| `Series[8]` | Trace colours, handed out in order as channels are plotted. Changing theme recolours every plotted channel from the new palette (a palette is only checked against its own background). |
| `RampCool` | Hue of the heat table's sequential scale and of its below-target (diverging) arm. |
| `RampWarm` | Hue of the above-target arm. |
| `Readout` (optional) | Ink for live values and table cells. Defaults to `Text`. |
| `Chrome` (optional) | Window frame: title bar, menu bar, status strip. Defaults to `Panel`. |
| `Grid` (optional) | Plot/chart rules. Defaults to `Blend(Background, Text, 0.13)`. |
| `Style` | A `ThemeStyle` (below). Defaults to `ThemeStyle::Modern`. |

#### Derived tokens (normative formulas)

`Blend(a, b, t)`: clamp `t` to [0,1]; per channel `round(a + (b - a) * t)` with **round-half-to-even**
(.NET `Math.Round`), as a byte. All colours are opaque sRGB bytes.

| Token | Formula | Used for |
|---|---|---|
| `IsDark` | `Luminance(Background) < 0.35` | Picks ramp direction, status lightness, title-bar dark flag. |
| `Hover` | `Blend(PanelAlt, Text, 0.14)` | Hovered button/menu row/list row ground. |
| `Faint` | `Blend(Muted, Background, 0.35)` | Supporting text: a channel's range, hints under controls. |
| `Scroll` | `Blend(Text, Panel, 0.45)` | Scrollbar thumb. |
| `ScrollHover` | `Blend(Text, Panel, 0.20)` | Hovered thumb. |
| `Selected` | `Blend(Panel, Accent, 0.22)` | Checked toggle / chip / selected list item ground. |
| `OnAccent` | whichever of `#0B0B0B` and `#FFFFFF` has the higher WCAG contrast on `Accent` (ties pick `#0B0B0B`) | Text on an `Accent` fill. |
| `Header` | `Blend(Panel, Background, 0.5)` | Category headers in the channel list. |
| `Lane` | `Blend(Background, Text, 0.08)` | Lane dividers in stacked plot mode. |
| `Cursor` | `Blend(Text, Background, 0.25)` | Plot cursor line. |
| `Card` | `Blend(Panel, Text, 0.04)` | Floating readout card. |
| `EmptyCell` | `Blend(Background, Text, 0.05)` | Never-visited cells in heat tables. |
| `Warning` | `FromOklch(IsDark ? 0.80 : 0.62, 0.15, 85°)` | Gauge warning band. Fixed hue: never from `Series`/`Accent`. |
| `Danger` | `FromOklch(IsDark ? 0.68 : 0.55, 0.20, 27°)` | Gauge danger band / redline. |
| `Nominal` | `FromOklch(IsDark ? 0.76 : 0.58, 0.13, 152°)` | Gauge in-range band. |
| `SequentialRamp[5]` | `Build(RampCool, 5, IsDark ? 0.50 : 0.78, IsDark ? 0.88 : 0.36, 1.00, 0.45)` | Heat table, count colouring. Index 0 = least visible against the surface. |
| `CoolArm[6]` | `[Blend(Background, IsDark ? white : black, 0.18)] ++ Build(RampCool, 5, IsDark ? 0.47 : 0.72, IsDark ? 0.76 : 0.43, 1.00, 0.85)` | Diverging scale, below target. Index 0 is the neutral "on target" colour. |
| `WarmArm[6]` | same with `RampWarm` | Diverging scale, above target. |

`Luminance` is WCAG relative luminance (sRGB → linear: `s/12.92` if `s <= 0.04045` else
`((s+0.055)/1.055)^2.4`; `0.2126 R + 0.7152 G + 0.0722 B`). `Contrast(a,b) = (max+0.05)/(min+0.05)`.

`Build(anchor, steps, startL, endL, fromChroma, toChroma)`: take the anchor's OKLCH `(_, C, H)`; clamp `C` to
[0.09, 0.20]; for `i` in `0..steps`, `t = i/(steps-1)` (0 when steps = 1), colour =
`FromOklch(startL + (endL-startL) t, C (fromChroma + (toChroma-fromChroma) t), H)`.
OKLab/OKLCH use Björn Ottosson's published matrices exactly as in `ColorMath.cs` (port the constants
verbatim); `FromOklab` clips each linear channel to [0,1] before the sRGB transfer, then rounds half-to-even
and clamps to a byte. Hue is in radians inside the maths; the status colours above are written in degrees.

`Readable(on, first, second)` returns `first` when `Contrast(on, first) >= Contrast(on, second)`.

The `w6-ui-base` gate should include oracle cases for every derived token of every theme (bit-exact bytes).

#### The 15 themes, in picker order

`midnight` is the default (also used when a stored or requested id is unknown).

| Id | Name | Group | Background | Panel | PanelAlt | Line | Text | Muted | Accent | Marker | RampCool | RampWarm |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `midnight` | Midnight | Dark | #14171C | #1B1F26 | #222831 | #2C333E | #DDE3EA | #828D9E | #4FC3F7 | #FFB34D | #3987E5 | #D03B3B |
| `graphite` | Graphite | Dark | #17181A | #1E1F22 | #26282C | #34363B | #E2E3E6 | #8B8E95 | #7EB6E8 | #E0A44A | #4A8FD0 | #C4523F |
| `daylight` | Daylight | Light | #FFFFFF | #F4F6F8 | #E8ECF0 | #CDD5DD | #1B2026 | #5A6672 | #0B6BCB | #B25E00 | #1E88E5 | #D0342C |
| `paper` | Paper | Light | #FBF7EF | #F3EEE3 | #EAE3D5 | #D8CFBD | #2A241B | #6B6152 | #A85A16 | #8A5A00 | #1565C0 | #C43B18 |
| `dracula` | Dracula | Editor | #282A36 | #21222C | #343746 | #44475A | #F8F8F2 | #9CA0B0 | #BD93F9 | #FFB86C | #7B8FE8 | #FF5555 |
| `nord` | Nord | Editor | #2E3440 | #272C36 | #3B4252 | #434C5E | #ECEFF4 | #A0A8B7 | #88C0D0 | #EBCB8B | #5E81AC | #BF616A |
| `solarized-dark` | Solarized Dark | Editor | #002B36 | #01313D | #073642 | #0E4A57 | #EEE8D5 | #93A1A1 | #268BD2 | #CB4B16 | #268BD2 | #DC322F |
| `solarized-light` | Solarized Light | Editor | #FDF6E3 | #F5EED9 | #EEE8D5 | #DCD4BC | #073642 | #657B83 | #268BD2 | #B58900 | #268BD2 | #DC322F |
| `monokai` | Monokai | Editor | #272822 | #21221C | #31322B | #414339 | #F8F8F2 | #A6A28C | #66D9EF | #FD971F | #4EA8C4 | #F92672 |
| `one-dark` | One Dark | Editor | #282C34 | #21252B | #31363F | #3E4451 | #CDD3DE | #828997 | #61AFEF | #D19A66 | #61AFEF | #E06C75 |
| `gruvbox` | Gruvbox Dark | Editor | #282828 | #1D2021 | #32302F | #504945 | #EBDBB2 | #A89984 | #83A598 | #FE8019 | #458588 | #CC241D |
| `tokyo-night` | Tokyo Night | Editor | #1A1B26 | #16161E | #24283B | #2F334D | #C0CAF5 | #787C99 | #7AA2F7 | #FF9E64 | #7AA2F7 | #F7768E |
| `contrast-dark` | High Contrast Dark | HighContrast | #000000 | #000000 | #1C1C1C | #FFFFFF | #FFFFFF | #E0E0E0 | #00E0FF | #FFD000 | #0088FF | #FF3B30 |
| `contrast-light` | High Contrast Light | HighContrast | #FFFFFF | #FFFFFF | #EDEDED | #000000 | #000000 | #2A2A2A | #0032C8 | #8A3A00 | #0032C8 | #C20000 |
| `retro` | Retro DOS | Retro | #000000 | #000000 | #00005A | #38C8D8 | #F0F0F0 | #A8A8A8 | #FF40FF | #FFFF55 | #2050FF | #FF4A3A |

Optional colours stated by a theme: only `retro` states any: `Readout #55FF55`, `Chrome #0000AA`,
`Grid #1E3CC8`.

Series palettes (8 each, in hand-out order):

| Id | Series |
|---|---|
| `midnight` | #56820F #9D4CAB #B58D00 #7582D5 #DE5222 #007FAA #BF3469 #06A4B5 |
| `graphite` | #B8496A #2A7DB7 #A87C05 #7F86D3 #55843F #7C5496 #24A59D #E16A33 |
| `daylight` | #D32F69 #007AD5 #CC8600 #8C22A8 #2B7A2F #6D7ED5 #E54918 #23A3B1 |
| `paper` | #31671B #8272EF #EA6900 #1263BE #F55E35 #7124A1 #29A08E #AA0F55 |
| `dracula` | #D3519E #757B00 #8563B3 #008E37 #687CBF #F64C4E #0090A5 #C48132 |
| `nord` | #B18D40 #008BA5 #B66951 #23A5A5 #BE6069 #5080BB #658842 #A5689C |
| `solarized-dark` | #687800 #666ABD #D75624 #2CA89F #DE3431 #0075BA #B58901 #C13D7A |
| `solarized-light` | #738402 #6368BA #E86636 #1E9F96 #E9403A #1380C6 #C12073 #B38806 |
| `monokai` | #018D7A #FE2D76 #7D61B4 #5F8502 #CC6286 #A19528 #0093A7 #D07801 |
| `one-dark` | #B38C40 #3779AD #DB6871 #26A4B2 #A2692C #7E8BD1 #587F38 #9660A6 |
| `gruvbox` | #737500 #00989D #B35707 #6FA05E #A75970 #B78602 #098165 #E12B18 |
| `tokyo-night` | #B4853D #486CBC #E1627B #27A3B4 #B05714 #4A9DCA #4B7501 #8361B9 |
| `contrast-dark` | #00A899 #FB494A #7B6BFE #038434 #AC00C5 #AF8E00 #017ABA #B95600 |
| `contrast-light` | #B88E42 #8900B5 #107234 #717DFF #E7462F #0359DB #AD0067 #21A3AD |
| `retro` | #FF40FF #40E0FF #FFFF55 #6A7BFF #55FF55 #FF8C1A #20C0A0 #FF4F6E |

Derived values, computed from the formulas above (a Python transcription of `ColorMath.cs`; the oracle is
authoritative if they ever differ by one in a channel):

| Theme | IsDark | Hover | Faint | Scroll | ScrollHover | Selected | OnAccent | Header | Grid | Lane | Cursor | Card | EmptyCell | Warning | Danger | Nominal |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `midnight` | yes | #3C424B | #5C6470 | #868B92 | #B6BCC3 | #264354 | #0B0B0B | #181B21 | #2E3237 | #24272C | #ABB0B6 | #23272E | #1E2126 | #EAB532 | #FC5950 | #6BC987 |
| `graphite` | yes | #404246 | #62656A | #8A8B8E | #BBBCBF | #33404E | #0B0B0B | #1A1C1E | #313235 | #27282A | #AFB0B3 | #26272A | #212224 | #EAB532 | #FC5950 | #6BC987 |
| `daylight` | no | #CBCFD4 | #949CA3 | #7D8084 | #464B50 | #C1D7EE | #FFFFFF | #FAFAFC | #E1E2E3 | #EDEDEE | #54585C | #EBEDF0 | #F4F4F4 | #AF7C00 | #CC2827 | #2E9052 |
| `paper` | no | #CFC8BB | #9D9689 | #847F75 | #524C43 | #E2CDB6 | #FFFFFF | #F7F2E9 | #E0DCD3 | #EAE6DE | #5E5950 | #EBE6DB | #F1ECE4 | #AF7C00 | #CC2827 | #2E9052 |
| `dracula` | yes | #4F525E | #737785 | #979899 | #CDCDCA | #433B59 | #0B0B0B | #242631 | #43454E | #393A45 | #C4C4C3 | #2A2B34 | #32343F | #EAB532 | #FC5950 | #6BC987 |
| `nord` | yes | #545A69 | #787F8D | #93979E | #C5C8CE | #3C4D58 | #0B0B0B | #2A303B | #474C57 | #3D434E | #BCC0C7 | #2F343E | #383D49 | #EAB532 | #FC5950 | #6BC987 |
| `solarized-dark` | yes | #274F57 | #60787C | #839691 | #BFC3B7 | #09455E | #0B0B0B | #002E3A | #1F444B | #133A43 | #B2B9AD | #0A3843 | #0C343E | #EAB532 | #FC5950 | #6BC987 |
| `solarized-light` | no | #CECFC0 | #9AA6A5 | #728986 | #375B60 | #C7D8D7 | #0B0B0B | #F9F2DE | #DDDDCE | #E9E7D6 | #44666A | #EBE7D3 | #F1ECDB | #AF7C00 | #CC2827 | #2E9052 |
| `monokai` | yes | #4D4E47 | #7A7767 | #979892 | #CDCDC7 | #304A4A | #0B0B0B | #24251F | #42433D | #383933 | #C4C4BE | #2A2B25 | #31322C | #EAB532 | #FC5950 | #6BC987 |
| `one-dark` | yes | #474C55 | #626874 | #80858D | #ABB0BA | #2F4356 | #0B0B0B | #242830 | #3D424A | #353942 | #A4A9B4 | #282C32 | #30343C | #EAB532 | #FC5950 | #6BC987 |
| `gruvbox` | yes | #4C4841 | #7B7164 | #8E8771 | #C2B695 | #333D3B | #0B0B0B | #222424 | #413F3A | #383633 | #BAAE90 | #252727 | #32312F | #EAB532 | #FC5950 | #6BC987 |
| `tokyo-night` | yes | #3A3F55 | #575A71 | #747994 | #9EA6CA | #2C354E | #0B0B0B | #181822 | #303241 | #272937 | #969EC1 | #1D1D27 | #222430 | #EAB532 | #FC5950 | #6BC987 |
| `contrast-dark` | yes | #3C3C3C | #929292 | #8C8C8C | #CCCCCC | #003138 | #0B0B0B | #000000 | #212121 | #141414 | #BFBFBF | #0A0A0A | #0D0D0D | #EAB532 | #FC5950 | #6BC987 |
| `contrast-light` | no | #CCCCCC | #757575 | #737373 | #333333 | #C7D2F3 | #FFFFFF | #FFFFFF | #DEDEDE | #EBEBEB | #404040 | #F5F5F5 | #F2F2F2 | #AF7C00 | #CC2827 | #2E9052 |
| `retro` | yes | #22226F | #6D6D6D | #848484 | #C0C0C0 | #380E38 | #0B0B0B | #000000 | #1E3CC8 | #131313 | #B4B4B4 | #0A0A0A | #0C0C0C | #EAB532 | #FC5950 | #6BC987 |

| Theme | SequentialRamp (5) | CoolArm (6) | WarmArm (6) |
|---|---|---|---|
| `midnight` | #0761BC #3F80CF #689EE2 #90BCF5 #B8DAFF | #3E4145 #0058B2 #226FC7 #3F85DC #599CF1 #73B4FF | #3E4145 #AB091B #C23434 #D9504C #EF6B64 #FF857C |
| `graphite` | #1E66A4 #4A83BA #70A0D0 #96BDE5 #BDDBFB | #414243 #125E9B #3173B0 #4B89C5 #64A0DA #7CB6F0 | #414243 #9E2E1D #B44836 #CA604E #E17966 #F7917E |
| `daylight` | #5BBCFF #4A9BEC #3A7BBC #2A5C8E #1B3F63 | #D1D1D1 #47A9FF #3292EC #1C7BD0 #0065B4 #005099 | #D1D1D1 #FF6A5B #EA5549 #CD4036 #B12B24 #961210 |
| `paper` | #6EB9FF #5998EA #4679BB #335A8D #223E62 | #CECBC4 #5BA6FF #488FEA #3579CE #2263B3 #0C4E98 | #CECBC4 #FF7352 #E35E3F #C74A2C #AC3618 #912000 |
| `dracula` | #4B5BAE #6879C3 #8797D8 #A8B6EC #CAD6FF | #4F505A #4352A5 #5768BA #6C7ECF #8295E4 #99ACFA | #4F505A #B00017 #C72A31 #DE4A49 #F56561 #FF807A |
| `nord` | #3D6595 #5E81AC #809EC4 #A2BBDB #C6DAF2 | #545962 #355C8C #4B72A1 #6187B6 #789DCC #90B4E1 | #545962 #923944 #A85059 #BE676E #D47E85 #EB969C |
| `solarized-dark` | #0068AC #3585C1 #63A2D6 #8CBFEA #B6DDFF | #2E515A #005FA3 #0775B7 #338BCC #51A2E2 #6CB9F7 | #2E515A #B00007 #C72B29 #DE4A42 #F5665B #FF8174 |
| `solarized-light` | #62C0FF #4F9EDC #3E7DAF #2D5E84 #1E405C | #CFCABA #4DACF6 #3995DA #247FBF #0769A5 #00548C | #CFCABA #FF665B #ED5249 #D03D36 #B32624 #970910 |
| `monokai` | #006F89 #3F8AA1 #6AA5BA #92C2D2 #BADFEC | #4E4F4A #006680 #1E7B95 #3E91AA #5AA7BF #75BDD5 | #4E4F4A #AC0046 #C4275B #DB4770 #F16387 #FF7D9D |
| `one-dark` | #0E68A3 #4484B9 #6CA1CF #93BEE4 #BADCFA | #4F5259 #005F9A #2775AE #448BC4 #5EA1D9 #77B8EF | #4F5259 #9B2D3C #B24751 #C85F67 #DE777E #F58F95 |
| `gruvbox` | #007277 #3D8D91 #69A8AB #91C4C6 #B9E0E2 | #4F4F4F #00696E #187E83 #3C9498 #58AAAE #73C0C4 | #4F4F4F #B00000 #C72C23 #DE4B3E #F56757 #FF8171 |
| `tokyo-night` | #3C5FAE #5C7DC3 #7D9BD7 #A0B9EC #C4D8FF | #43444D #3456A4 #496CB9 #5F82CE #7699E3 #8DB0F9 | #43444D #9F2446 #B6405A #CC5970 #E37286 #F98B9D |
| `contrast-dark` | #005ED0 #217EE2 #569EF3 #83BDFF #AFDBFF | #2E2E2E #0054C6 #006CDB #1A84F0 #3F9CFF #5DB3FF | #2E2E2E #B00000 #C72C23 #DE4B3E #F56757 #FF8171 |
| `contrast-light` | #73B1FF #5E91FF #4A73CC #36569B #243B6C | #D1D1D1 #619DFF #4F86FF #3D70E4 #2C5AC7 #1B45AB | #D1D1D1 #FF6755 #EC5343 #CF3D30 #B3271D #970B06 |
| `retro` | #2A55D3 #4C77E4 #6F97F5 #94B7FF #BBD7FF | #2E2E2E #224BC9 #3764DD #4D7CF2 #6494FF #7CACFF | #2E2E2E #B00000 #C72C21 #DE4B3C #F56756 #FF816F |

#### ThemeStyle

The non-colour half of a theme. A theme that states no style gets `Modern`.

| Field | `Modern` (every theme except Retro) | `retro` | Rule |
|---|---|---|---|
| `UiFont` | Segoe UI | OLV Retro, falling back to Consolas glyph by glyph | Labels, menus, buttons, tooltips, context menus, and the text the custom-drawn views draw for axes and labels (plot, histogram, scatter, curve, gauge). |
| `DataFont` | Consolas | OLV Retro → Consolas | Numbers that line up in columns: tune-table cells, readouts, status-bar values. The tune table draws its cells in `DataFont` (normal and bold). |
| `CornerScale` | 1 | 0 | Multiplies every named corner radius (below). 0 squares the application off. |
| `PixelText` | false | true | Text is rasterised to whole pixels with no smoothing (WPF: aliased rendering, display formatting), everywhere, custom-drawn views included. |
| `Table` | `Heat` | `Grid` | `Heat`: each tune-table cell is filled from the sequential ramp by value. `Grid`: unfilled cells on a ruled grid, values in the `Readout` ink (see w7-calibration). |

Fonts on Linux/macOS: Segoe UI and Consolas are Windows fonts. **Open question (user):** which bundled or
system fonts stand in for them off Windows (and whether to bundle the same fallbacks on Windows for
identical metrics). Builders: until decided, use egui's default proportional font for `UiFont` and its
default monospace for `DataFont` in Modern themes.

Named corner radii (in device-independent pixels before zoom), each multiplied by `CornerScale`:

| Name | Radius | Used by |
|---|---|---|
| `CornerSmall` | 3 | chips, combo boxes, jump buttons, scroll thumbs |
| `Corner` | 4 | buttons, toolbar toggles, menu rows |
| `CornerMedium` | 5 | (available) |
| `CornerLarge` | 6 | menus, context menus |
| `CornerPill` | 9 | saved-selection (preset) chips |

**Open question (builder can settle, flag to user):** a few custom-drawn views use literal radii that the C#
does not scale by `CornerScale` (plot readout card 4, its hovered row 3, gauge face 8), so in Retro they stay
rounded. The Rust should either copy that or scale them; see UI-SPEC-OPEN-QUESTIONS.md.

#### The OLV Retro font

- File: `rust/assets/fonts/OLVRetro-8x16.ttf` (byte-identical to the C#'s `Fonts/OLVRetro-8x16.ttf`), family
  name **"OLV Retro"**. Built by `olv-fontgen` from `olv-retro-8x16.txt`; never edit the TTF by hand. MIT,
  every glyph original (see `rust/assets/fonts/README.md`).
- Monospaced 8×16 pixel grid, baseline under row 12, rows 13–16 descender space.
- Coverage: 146 glyphs: printable ASCII U+0020–U+007E plus
  `NBSP ¢ § ° ± ² ³ µ · × ÷ ƒ Δ Ω γ θ λ π – — ‘ ’ “ ” • … ‹ › ← ↑ → ↓ ↔ − √ ∿ ≤ ≥ ⌸ ■ ▲ ▴ ▸ ▼ ▾ ◂ ◈ ○ ● ✓ ✕`
  and `.notdef`. Any other character falls back, per glyph, to the monospace fallback font (never a box).
- Embed it in the binary (`include_bytes!`); register it with egui's font definitions as the first family of
  both the proportional and monospace stacks while a Retro-styled theme is active, with the fallback font
  after it.
- The C# draws the pixel font at the interface's ordinary text sizes (11, 12, 19 px…), not only at 16, with
  aliased rasterisation. **Open question (user):** whether the Rust should snap Retro text to multiples of
  16 px (crisp, but changes layout) or follow the C# sizes (some glyphs uneven). egui antialiases glyphs; to
  get pixel text the builder must rasterise the TTF without smoothing (e.g. threshold coverage at 50%) for
  Retro only.

### Applying a theme

- The active theme is held in one place (`ThemeManager.current`). Applying a theme replaces **every** token
  at once (nothing from the previous theme survives), then notifies every self-drawn view, which re-reads its
  brushes, fonts and ramps and repaints. In egui terms: rebuild `egui::Visuals` and the font definitions from
  the theme and request a repaint; self-drawn views read the theme each frame.
- Changing theme also recolours every plotted channel from the new `Series` palette (by the order the
  channels were assigned colours), except channels the user gave an explicit colour (see w7-plot).
- Choosing a theme in the menu applies it and stores its id (`settings.json` → `ThemeId`, trimmed; empty
  means default). `--theme <id>` applies it for this run only and does not store it. Applied before the
  window is first shown, so no frame is drawn in the stored theme.
- Every window the application opens (main, Calculators, Power, Insights, Project, Faults, Settings dialog)
  follows the theme and the zoom automatically, including windows opened later; no window may open in the
  default font or colours.

### Title bar colour (TitleBar)

Best effort, Windows only. On theme change and when a window is created: set the OS dark-title-bar flag from
`IsDark`; on Windows 11 also set caption colour = `Chrome`, caption text = `Text`, border = `Line`.
Failures are ignored silently (older Windows keeps the system title bar). On Linux/macOS: **Open question
(user):** keep native decorations (colour not controllable) or draw a custom title bar. Default for builders:
native decorations, and set eframe's `ViewportBuilder` theme hint from `IsDark` where supported.

The window *title text* is specified in w9-shell.

### Shared widget styles

Every screen uses these. Sizes are design pixels before zoom.

| Widget | Rest | Hover | Checked / selected | Disabled | Other |
|---|---|---|---|---|---|
| Text label | `Text`, `UiFont` | – | – | – | |
| Button | ground `PanelAlt`, 1 px `Line` border, radius `Corner`, padding 12×5, text `Text`, hand cursor | ground `Hover` | – | whole button at 40% opacity | content centred |
| Toolbar toggle | as Button | ground `Hover` | ground `Selected`, border `Accent`, text `Accent` | 40% opacity | |
| Text box | ground `Bg`, 1 px `Line` border, padding 6×4, text and caret `Text` | – | – | – | |
| Check box | text `Text` | – | – | – | |
| Chip (segmented radio, e.g. channel sort) | transparent, 1 px `Line`, radius `CornerSmall`, padding 8×3, 4 px right gap, 11 px text `Muted` | text `Text` | ground `Selected`, border `Accent`, text `Accent` | – | |
| Jump button (▲/▼ on channel rows) | 26×24, ground `PanelAlt`, 1 px `Line`, radius `CornerSmall`, 1 px side margins, 12 px bold `Muted` | ground and border `Accent`, text `OnAccent` | – | 25% opacity | always visible, never hover-only |
| Preset chip | ground `PanelAlt`, 1 px `Line`, radius `CornerPill`, padding 9×3, 4 px right and bottom gap, 11 px `Text`, max text width 150 with ellipsis | ground `Hover`, border and text `Accent` | – | – | |
| Combo box (closed) | ground `Bg`, 1 px `Line`, radius `CornerSmall`, padding 7×4, 11 px `Text`, small down-chevron in `Muted` at the right, content inset 8 left / 22 right, ellipsis | border `Accent` | – | – | opens on press |
| Combo drop-down | ground `PanelAlt`, 1 px `Line`, radius `CornerSmall`, max height 360 then scrolls, at least as wide as the box, zoomed with the interface | item ground `Hover` | item ground `Selected` | – | item padding 9×5 |
| Menu bar | transparent on `Chrome`, `UiFont`, `Text` | | | | items padding 10×6 |
| Menu / submenu | ground `PanelAlt`, 1 px `Line`, radius `CornerLarge`, padding 4; rows radius `Corner`; a 20 px tick column showing `✓` in `Accent` when checked; a `›` in `Muted` at the right of items with children (never on menu-bar names, which open *below* themselves, offset 2 px; submenus open to the right, offset −3, −8) | row ground `Hover` | ✓ | text `Muted` (disabled items are used as captions; they must read as text) | arrow keys cycle within a menu |
| Context menu | same as a menu | | | | |
| Separator | 1 px `Line`, margin 8×4 | | | | |
| Tooltip | `UiFont`, zoomed with the interface | | | | |
| Scrollbar | 11 px wide, transparent track (still clickable: page up/down), no arrow buttons; thumb `Scroll`, radius `CornerSmall`, 2 px inset | thumb `ScrollHover` | dragging: thumb `Accent` | | |
| Group header (channel categories) | ground `Header`, 1 px `Line` top and bottom, padding 6×5, chevron (down when expanded, right when collapsed) 8 px `Muted` | ground `Hover`, chevron `Text` | | | toggles the group |

**Menus that scroll.** Any menu or submenu taller than the screen's work area (the desktop minus the
taskbar) scrolls vertically inside its popup, capped at the work-area height; menus that fit never show a
scrollbar. Reason: at 150% zoom the View menu ran off the bottom and took Zoom, the way back, with it.
In egui, wrap every menu body in a `ScrollArea::vertical().max_height(screen work-area height)`.

### Interface zoom (InterfaceScale)

- Steps: **1.00, 1.15, 1.25, 1.50, 1.75, 2.00** (shown as `100%`, `115%`, `125%`, `150%`, `175%`, `200%`;
  label = `{scale*100:0}%`).
- `Snap(x)`: the step nearest `x` (first of equal distances); non-finite → 1.00.
- `Larger(x)`: first step greater than `Snap(x)` + 1e-9, else 2.00. `Smaller(x)`: last step less than
  `Snap(x)` − 1e-9, else 1.00.
- It is a zoom of **everything** in every window (text, fixed widths, paddings, custom-drawn views, popups,
  menus, drop-downs, tooltips), not a font size: the windows hold about two hundred fixed text sizes and as
  many fixed widths, so scaling text alone would overflow them. Text and self-drawn views redraw at the new
  size (never a stretched bitmap). In egui: `ctx.set_zoom_factor(scale)` (multiplies `pixels_per_point`);
  do not also scale fonts.
- Stored as `UiScale` in settings (omitted when 1). The store accepts 1–3 and clamps; the app snaps to the
  steps on load. `--zoom <x>` (invariant-culture number) applies `Snap(x)` for this run only, not stored.
- Menu items and shortcuts (Ctrl +, Ctrl −, Ctrl 0) are specified in w9-shell.

### Workspace modes

`WorkspaceMode`: `Log` (reading a recording), `Gauges` (live dials), `Calibration` (the tune), `Guide`
(built-in help, a mode so it works offline in a garage). Inside Log, `LogView`: `Plot` (traces against
time), `Histogram` (binned into a table shaped like a tuning table), `Scatter` (every sample at its X and Y,
coloured by a third channel). Switching rules are in w9-shell.

### Exporting a view as PNG (ImageExport)

- Renders the view itself (not a screen grab) at **2×** its current size in design pixels (sizes rounded
  **up**, so the axis line in the last row/column survives), onto the given background colour first (so a
  transparent view never saves see-through).
- Writes to `<path>.tmp` then renames over `<path>` (never leaves a half-written PNG where the user asked
  for one).
- A view with zero width or height fails with the message `The view has no size to save.`
- In egui: render the view's painter output offscreen (or crop a viewport screenshot at the view's rect
  with `pixels_per_point = 2 × zoom`); it must not include overlapping windows.

### State plumbing (Mvvm)

The C# `ObservableObject`/`RelayCommand` pattern has no counterpart to port: in egui, screens read the
application state each frame. Each `Can…` rule in this spec is evaluated every frame to enable or disable its
control, so no "requery" mechanism is needed.


## §2 `w7-plot`: Log mode, the plot and the channel sidebar

### Scope and where it sits

This package is the **Log mode, Plot view**: the time-series plot (`olv::plot`, was `LogPlot`), the channel sidebar beside it (search, sort, channel rows, saved selections, fixed scale, calculated channels), the find bar above the plot, and the channel row model (`ChannelItem`). It also covers the filter row model (`FilterItem`); the filter *panel* is shown only in the Histogram/Scatter views (the w-histogram package owns its layout), but filters change what the find bar searches, so their rules are given here too.

Log mode is shown when `MainViewModel.InLogMode` (workspace mode = Log). Inside it, a three-column grid:

| Column | Width | Content |
|---|---|---|
| 0 | 300 px initially, min 200 | Channel sidebar (when `ShowLog`) or the axis/filter panel (when `ShowAxisPanel`, i.e. Histogram or Scatter view) |
| 1 | 4 px | Draggable splitter, fill `Line`. Dragging resizes column 0 (min 200) against column 2 (min 300) |
| 2 | rest, min 300 | Plot view (when `ShowLog`): the find bar docked on top, the plot filling the rest. Histogram or Scatter otherwise |

`ShowLog` is true when the log view is "Plot" (the default). It is chosen by the toolbar chips "Log / Histogram / Scatter" and by View > Plot / Histogram / Scatter (those menu items are enabled only when `InLogMode`). `ShowLog` does not depend on the workspace mode: it stays true while the user is in Gauges/Calibration mode, the Log grid is just hidden.

---

### The plot (`olv::plot`)

A custom-drawn time-series view. Each trace is scaled to its own vertical range; there is **no shared Y axis**. Values are read from the hover card and the sidebar rows.

#### Geometry

- Whole control is clipped to its bounds and takes keyboard focus (it grabs focus on left mouse down, but handles no keys itself).
- Constants: `Pad = 8`, `AxisHeight = 26`, `LaneGap = 6`, `HoverTolerance = 40`.
- **Plot area** = `x = 8, y = 8, width = max(1, W - 16), height = max(1, H - 26 - 16)` where W, H are the control size. The bottom 26 px plus padding hold the time-axis labels.
- Time maps linearly across the area: `x = area.Left + (t - viewStart) / (viewEnd - viewStart) * area.Width`, and back.
- **Value to y** (the single mapping used by drawing and hit-testing; they must agree exactly):
  `(min, range) = channel.scale(hold_steady)`; `top = area.Top + 3`; `height = max(1, area.Height - 6)`; `y = top + height * (1 - (value - min) / range)`. Values are **not clamped**: a value outside the drawn range is drawn outside it (see Open questions on pinned ranges in stacked lanes).

#### Theme tokens used (rebuilt on every theme change, then repaint)

| Use | Token |
|---|---|
| Ground (whole control) | `Background` |
| Axis labels, hint text, cursor label, lane range labels | `Muted` |
| Vertical grid lines and baseline, 1 px | `Grid` |
| Cursor line, 1 px | `Cursor` |
| Markers: dashed line 1 px, dash 3 on / 3 off; marker text | `Marker` |
| Find/traced-cell shading ("occurrences") | `Marker` at alpha 0x33 |
| Selection fill | `Accent` at alpha 0x38 |
| Selection edges (1 px) and selection label | `Accent` |
| Lane separators, 1 px | `Lane` |
| Hover card fill | `Card` at alpha 0xF2 (just off opaque on purpose) |
| Hover card border, 1 px | `Line` |
| Hover card labels | `Muted`; jump-able labels `Accent` |
| Hover card values | `Readout` |
| Hover card row under pointer | `Hover` |
| Font for all plot text | the theme style's UI font, normal weight |
| Traces | each channel's own colour (from the theme's `Series` palette or a pinned colour) |

#### What is drawn, in order

1. Fill the whole control with `Background`.
2. **No usable log** (no document, or the document has 1 sample or fewer): draw the centred hint `Open a datalog to begin  (Ctrl+O)` (two spaces before the bracket), 13 pt, `Muted`, centred in the whole control. Stop.
3. If the plot area is narrower or shorter than 4 px: stop.
4. **Time axis** (always drawn once a log is open; see below).
5. If no channel is ticked: draw the centred hint `Tick channels on the left to plot them` (13 pt, `Muted`, centred in the whole control, over the axis). Stop.
6. **Traces**, for the ticked channels in sidebar-collection order (the order of `MainViewModel.Channels`, not the sorted list view):
   - Stacked: for each channel draw the lane chrome, then its trace (heavy pen if it is the hovered one).
   - Overlaid: draw every trace except the hovered one with its normal pen, then the hovered trace last with its heavy pen, so it sits on top.
7. **Occurrences** (shaded spans).
8. **Selection** (shaded span + edges + label).
9. **Markers**.
10. **Cursor** (line + time label).
11. **Hover card**.

Pens: normal trace 1.4 px, hovered ("highlight") 2.8 px, both in the channel colour; segments are straight with no smoothing joins.

#### Time axis

- `span = viewEnd - viewStart`; `step = nice_step(span / 8)` where `nice_step` rounds up to 1, 2, 5 or 10 times a power of ten (`magnitude = 10^floor(log10(raw))`, `norm = raw / magnitude`, pick 1 if norm <= 1, 2 if <= 2, 5 if <= 5, else 10). A non-positive or NaN raw step gives 1.
- Ticks at `t = ceil(viewStart / step) * step`, then every `step`, while `t <= viewEnd`. A tick whose x is more than 1 px outside the area is skipped.
- Each tick: a vertical `Grid` line from area top to area bottom, and a label, 11 pt `Muted`, centred on x, top at `area.Bottom + 6`.
- After the ticks, a horizontal `Grid` baseline along the area bottom. There are **no horizontal grid lines and no Y axis**.
- Label format: one format for the whole axis. **Clock format is used when `viewEnd >= 60`**, otherwise seconds.
  - `decimals = 0 if step >= 1; 1 if step >= 0.1; 2 if step >= 0.01; else 3`.
  - Seconds format: value with `F{decimals}`, invariant culture, followed directly by `s` (no space), e.g. `12.5s`.
  - Clock format: `{whole minutes}:{seconds:00}`; when decimals > 0 add `.{milliseconds:000}` (always three digits, whatever `decimals` is). Negative times are shown as 0.

#### Traces and min/max decimation

Built afresh on every paint (no cache other than the smoothing cache on the channel row):

- Sample range: `i0 = max(0, index_at(viewStart) - 1)`, `i1 = min(len, index_at(viewEnd) + 2)` (exclusive). `index_at(t)` returns the **last sample whose time is <= t** (binary search; 0 when t is before the first sample).
- Value per sample is `ChannelItem.value_at(i)` (smoothed if the row is smoothed, else as logged).
- **Pen lift** (discontinuity): when the value is NaN, or when `time[i] - time[i-1] > gap_threshold`. `gap_threshold = 10 x the median sample interval` of the document (infinite when there are fewer than 3 samples or no positive interval). At a lift the pending column is emitted, the figure is closed, and a NaN sample is skipped; a non-NaN sample after a time gap starts a new figure.
- **Decimation**: each sample's x is rounded to an integer pixel column. Consecutive samples in the same column are folded into that column's min and max. When the column changes, the finished column is emitted as two points `(column, y(max))` then `(column, y(min))` (the second omitted if equal). The first point of a figure starts the figure; later points are joined by straight lines. This keeps work linear in pixel width and keeps spikes.
- Note the points are drawn at the rounded column, not the exact x.

#### Stacked lanes vs overlaid

- `StackedLanes` (view model) chooses. Overlaid is the default (not remembered between runs).
- Overlaid, or stacked with only one ticked channel: every trace uses the whole plot area.
- Stacked with N > 1 ticked channels: lane i is `top = area.Top + i * (area.Height / N)`, `height = max(1, area.Height / N - 6)`, full width.
- **Lane chrome** (drawn whenever stacked, including N = 1 where the lane is the whole area):
  - For lanes after the first: a `Lane` separator line across the width at `lane.Top - 3`.
  - If the lane is at least 26 px tall: channel name, 10 pt, channel colour, at `(lane.Left + 4, lane.Top + 1)`; the top of the drawn range `format(min + range)` 9 pt `Muted`, right-aligned 2 px from the lane's right edge at `lane.Top + 1`; the bottom of the drawn range `format(min)` 9 pt `Muted`, right-aligned, bottom 1 px above the lane bottom. These are the range the lane is drawn over (pinned or steady-widened), formatted with the channel's digits in the row's unit system, **without units**.
- Hit-test: overlaid uses proximity (see Hover); stacked uses the lane under the pointer. For lane lookup the gaps are ignored (`lane = clamp(floor((y - area.Top) / (area.Height / N)), 0, N-1)`), so there is no dead band. A pointer above or below the area in stacked mode hits nothing.

#### Vertical scale per trace

`ChannelItem.scale(hold_steady)`:

- If the row has a pinned range `(lo, hi)`: `(lo, hi - lo)`, used exactly.
- Else `TraceScale::for(channel.min, channel.max, hold_steady)`:
  - `range = max - min`; if NaN, infinite or <= 0: `(min - 0.5, 1)` (a flat line through the middle).
  - If not hold_steady: `(min, range)`.
  - `centre = (min + max) / 2`, `floor = |centre| * 0.05`. If `range >= floor` or `floor <= 0`: `(min, range)`. Otherwise `(centre - floor / 2, floor)` (centred, widened to 5 % of the magnitude).
- `hold_steady` is the global switch View > "Draw steady channels as steady": checkable, **on by default**, not remembered between runs. Tooltip: `A sensor that barely moves is drawn flat rather than having its last decimal stretched to fill the lane. Turn off to see the true range`. Toggling repaints the plot.

#### Occurrences (shaded spans)

A list of `(from, to)` second pairs set by the find bar and by tracing a histogram cell or scatter mark back to the log. For each span overlapping the view: `left = max(area.Left, x(from))`, `right = min(area.Right, x(to))`; if narrower than 1.5 px it is widened to 1.5 px (so a one-sample visit shows); fill full area height with `Marker` at alpha 0x33.

Cleared by: closing the find bar, toggling find off, a find that fails, `ClearSelection` (any time the selection is dropped, see below), and loading a new log.

#### Selection (marked span)

- Exists when `from` is a number and `to > from`.
- Drawn: fill `Accent` alpha 0x38 between `max(area.Left, x(from))` and `min(area.Right, x(to))` over full height (nothing if that is empty), a 1 px `Accent` line at each edge, and a label `{span:F2} s   ·   {count:N0} samples` (three spaces either side of the middle dot), 11 pt `Accent`, at `x = clamp(left + 6, area.Left, max(area.Left, area.Right - labelWidth - 2))`, `y = area.Top + 20`. `span = to - from`; `count = index_at(to) - index_at(from) + 1`.
- Every change is reported to the view model as an inclusive sample span `(index_at(from), index_at(to))`, or "none" when there is no selection (`MainViewModel.UpdateSelection`). Reports are sent continuously while dragging.

#### Markers

For each log marker whose time is inside the view (inclusive), in document order:

- A dashed `Marker` vertical line over the full area height.
- A label is drawn only if this marker is at least 15 px right of the last marker that got a label. Label: marker text, 10 pt, `Marker` colour, rotated -90 degrees (reads upward) about the point `(x + 4, area.Bottom - 6)`, single line, maximum length `max(20, area.Height - 12)` px, trimmed with an ellipsis.

#### Cursor

- Shown when a cursor time exists and its x is inside the area.
- A 1 px `Cursor` line over the area height, and a label `{time:F3} s`, 11 pt `Muted`, at `x = min(cursorX + 5, area.Right - labelWidth)`, `y = area.Top + 2`.
- The cursor always sits on a sample: `index = index_at(pointer time)`, `time = time[index]` (the sample at or before the pointer, see Open questions).

#### Hover card (readout)

Shown when a trace is hovered, it is still ticked, and a cursor index exists.

- Title: channel name, 12 pt, channel colour.
- Three rows, label then value:
  - When the row has selection statistics with data (a span is marked): `avg`, `max`, `min` = mean, max, min **over the span**, each `format_with_units`.
  - Otherwise: `now` = `format_with_units(value_at(cursor))` (the smoothed value if smoothed); `max` = `{format_with_units(channel.max)}  @ {clock(time of channel.max_index)}`; `min` likewise (two spaces before `@`). Max/min are the whole-log, as-logged extremes, first occurrence.
  - `clock(s)`: if `s < 60`: `{s:F2} s`; else `{floor(s / 60)}:{s mod 60:00.0}`.
- `format_with_units(v)`: `format(v)` then a space and the unit label if the unit label is not empty. `format(v)` is `—` for NaN, else the value converted to the row's unit system, with `F{digits}` where digits is the channel's declared decimals clamped to 0..6.
- **Jump rows**: `max` and `min` are clickable when the channel has max/min indexes >= 0, is not flat, and no span statistics are showing. Their labels are `Accent`, other labels `Muted`. Values 11 pt `Readout`, labels 10 pt.
- Layout: padding 8, label/value gap 8, row height 15. `width = 16 + max(titleWidth, maxLabelWidth + 8 + maxValueWidth)`; `height = 16 + titleHeight + 3 + 3 * 15`.
  - `x = cursorX + 14`; if that would pass the area's right edge, `x = cursorX - width - 14`; then clamp into `[area.Left, area.Right - width]`.
  - `y = clamp(traceY - height / 2, area.Top, area.Bottom - height)`, where `traceY` is the y of the hovered trace's value at the cursor, using that channel's lane when stacked.
  - Rounded rectangle, corner radius 4. Title at `(x + 8, y + 8)`. Rows start at `y + 8 + titleHeight + 3`; label at `(x + 8, row + 1)`, value at `(x + 8 + maxLabelWidth + 8, row)`.
  - Each jump row's hit rectangle is `(x + 3, row - 1, width - 6, 15)`; while the pointer is inside it, that rectangle is filled `Hover` with radius 3 behind the row. Hit rectangles are recomputed on each paint and empty when no card is drawn.

#### Hover detection

On each pointer move (not while panning or selecting):

- If the pointer is inside a jump-row hit rectangle: show the hand pointer and **keep the current cursor and hovered trace** (so the card does not slide away); repaint only.
- Otherwise: crosshair pointer; set the cursor index from the pointer x; raise "cursor sample changed" with the index; find the hovered trace:
  - Overlaid: among ticked channels whose `value_at(index)` is not NaN, the one whose y is nearest the pointer's y, if nearer than 40 px (strictly). Ties go to the earlier channel.
  - Stacked: the channel of the lane under the pointer (none if outside the area vertically).
- If the hovered trace changed, raise "hover channel changed"; the view model then marks exactly that row as highlighted (`ChannelItem.IsHighlighted`), all others not.

On pointer leaving the plot: cursor cleared (no cursor line, no card), hovered trace cleared and reported as none. The rows' values and the status-bar cursor text are **not** cleared (see Open questions).

#### Mouse and keyboard

There are no keys handled by the plot itself and no right-click menu on the plot. No horizontal-scroll or modifier variants of the wheel.

| Input | Condition | Effect |
|---|---|---|
| Move | log has >= 1 sample | Selecting: update the selection from the anchor to the pointer time (min/max ordered), report, repaint; no cursor/hover update. Panning: shift the view. Otherwise hover detection above. |
| Left down on a jump row of the card | a trace is hovered | Jump: `focus_time(time of max_index or min_index)`. Nothing else happens on that click. |
| Left double-click | anywhere else | Fires "view changed by user" (stops live follow), then **reset view** to the whole log. (The first click of the pair has already started a pan and cleared any selection.) |
| Shift + left down | log has >= 1 sample | Start marking: anchor = pointer time, from = to = anchor; capture the mouse; pointer becomes horizontal-resize (west-east). Repaint. |
| Left down (plain) | | If a selection exists, clear it (also clears occurrences). Start panning: fire "view changed by user" immediately (even if the mouse never moves), remember the anchor x and the current view; capture the mouse; pointer becomes the west-east scroll pointer. |
| Left up | | If marking: stop; if the span has zero width (shift-click without drag) clear the selection, else report it once more. Stop panning; release capture; pointer back to crosshair. |
| Pan drag | panning | `perPixel = (startViewEnd - startViewStart) / area.Width`; `shift = (anchorX - pointerX) * perPixel`; view = start view + shift; clamp. (Dragging right moves to earlier time.) |
| Wheel | log has >= 1 sample | Fire "view changed by user". `focus = time at pointer x`; factor = `1 / 1.25` when the wheel delta > 0 (zoom in), `1.25` otherwise (zoom out), one step per event whatever the delta size. `viewStart = focus - (focus - viewStart) * factor`; `viewEnd = focus + (viewEnd - focus) * factor`; clamp. |

Pointer shapes: crosshair over the plot by default; hand over a jump row; west-east resize while marking; west-east scroll while panning.

#### View operations

- **Clamp view** (after pan, wheel, `zoom_to`, `focus_time`): `lo = time[0]`, `hi = time[last]` (if `hi <= lo`, `hi = lo + 1`). `minSpan = max((hi - lo) / 100000, 0.0001)`; if the view is narrower, re-centre it at `minSpan`. Then `span = min(viewEnd - viewStart, hi - lo)`; if `viewStart < lo` shift to start at lo; if `viewEnd > hi` shift to end at hi.
- **Reset view**: whole log, `time[0]` to `time[last]` (end = start + 1 if they are equal); with no log, 0 to 1. Not clamped further.
- **`focus_time(t)`**: keep the current span, centre it on t, clamp; park the cursor on `index_at(t)` and raise "cursor sample changed". Does **not** fire "view changed by user".
- **`zoom_to(a, b)`**: view = ordered (a, b), clamped. Does not fire "view changed by user".
- **`select_range(a, b)`**: requires >= 1 sample; ends any drag-marking; selection = ordered (a, b); reports and repaints. Not clamped to the log.
- **`clear_selection()`**: does nothing if there is no selection, no marking in progress and no occurrences. Otherwise drops the selection **and the occurrences**, reports "none", repaints.
- **`set_occurrences(spans)`**: replace the shaded spans and repaint.
- **`set_document(doc, rows)`** (opening a file): replaces document and rows; drops selection, marking, occurrences, hover and cursor; resets the view.
- **`extend_document(doc, rows, follow)`** (live tick): replaces document and rows, keeps everything else; with 0 samples just repaint. If `follow`: `viewEnd = newest`, `viewStart = max(oldest, newest - follow_span)`; if the span is under 1e-6 s, `viewEnd = viewStart + 1`. `follow_span` is a fixed 30 s. When not following, the view is left exactly as it was (not clamped).
- Exposed: `view_start`, `view_end` (the Histogram/Scatter "Only the zoomed time range" option reads these), `has_selection`.
- **Reset zoom** command (View > Reset zoom, enabled when `ShowLog`): when live, sets "follow" back on (the view jumps to the newest 30 s on the next tick); otherwise reset view to the whole log.

#### Events the plot raises (wired to the view model)

| Event | Handler and result |
|---|---|
| cursor sample changed (index) | `MainViewModel.UpdateCursor(index)`: ignored with no log or index < 0; status-bar cursor text = `{time:F3} s   (sample {index:N0})` (three spaces); every row's value updated (see Channel rows). |
| hover channel changed (row or none) | `MainViewModel.HighlightChannel`: that row highlighted, all others not. |
| selection changed (span or none) | `MainViewModel.UpdateSelection` (below). |
| view changed by user | The window clears its live "follow" flag. |

`UpdateSelection(span)` (ignored with no log): stores `Selection`; refreshes `ExportScope` (`marked span, {count:N0} samples` or `whole log`). With a span: every row gets statistics over the span (min, max, mean of the as-logged channel); status-bar cursor text = `{to - from:F3} s selected   ({count:N0} samples)`; Hint = `Marked {from:F2}–{to:F2} s. Rows show min … max and average over the span. Click the plot to clear.` (from/to are the times of the first and last sample of the span). With none: rows drop their statistics, cursor text = `—`, Hint back to the default hint.

The default hint (status strip):

```
Scroll = zoom  •  Drag = pan  •  Double-click = fit  •  Hover a trace for its min/max, then click either to jump there  •  Shift-drag to mark a span and summarise it  •  Right-click for more
```

(two spaces either side of each bullet).

#### Live session: following new data

- The window keeps a `follow` flag, set **on** whenever a live session starts, and off on any "view changed by user" (wheel, any plain left-press in the plot, double-click).
- A 200 ms timer runs while live. Each tick: `MainViewModel.RefreshLive()`; if it reports nothing new, skip. If the session has stopped, tear down. Otherwise `extend_document(document, rows, follow)` (and, when the plot is not the view on screen, the histogram/scatter is rebuilt on every 5th tick).
- `RefreshLive` takes a snapshot; with 0 samples nothing happens. The first time there are samples **and the row list is empty**, rows are created for every non-time channel, colours handed out in order, saved styles applied, and the default channels `RPM, MAP, TPS, AFR, CLT` ticked where present (by case-insensitive name, flat or not; no fallback). Calculated channels are **not** built for a live session. On later ticks every row is re-pointed at the longer channel of the same name (colour, tick and style kept; the row's smoothing cache dropped so a smoothed trace keeps growing). When the number of non-flat channels changes, the list filter is re-run (so channels that start moving appear under "Hide unused").
- While following, a channel's range keeps widening as new data arrives, so traces rescale as the session runs (by design: scale is the whole-session min/max).
- Connect hint: `{opening} The plot follows the newest data until you zoom or pan.` followed by, when the session has at least one channel, `  Untick "Hide unused" to see every channel — all of them are being recorded either way.` (two leading spaces), followed by an ambiguity note owned by the connection package. `{opening}` is `Recording to {path}.` when recording, else `Watching only — nothing is being written. Press Record when you want a log.`
- Disconnect leaves the last snapshot on the plot; Hint `Disconnected. The session is still here, and its recording is on disk.` (only when a document exists).
- The plot shows `Open a datalog to begin  (Ctrl+O)` until the session has at least 2 samples.

#### Export as image

File > Export > "Plot as PNG…" (visible when `ShowLog`; the Export menu is enabled when `CanExport` = a document exists). Save dialog, suggested name `{log file name without extension}-plot.png` (`log-plot.png` with no file path), filter `PNG image|*.png|All files|*.*`, starting in the workspace exports folder, overwrite prompt on. The plot is rendered at its current size with a `Background` backdrop. Success Hint: `Saved the plot to {file name}`. Failure (I/O, access, not supported, invalid operation): message box, title `OpenLogViewer`, OK, warning icon, text `Could not save the file.\n\n{error message}`. The CSV exports beside it are owned by the export package.

---

### Find bar

Docked across the top of the plot column. Visible when `MainViewModel.Finding` is true (and the plot column is visible, i.e. `ShowLog`). Background `Panel`, 1 px bottom border `Line`, padding 8 horizontal / 6 vertical.

Left to right:

| Element | Details |
|---|---|
| Label `Find` | 11 pt `Muted`, 7 px right margin |
| Condition box | fills the remaining width, minimum 180 px; bound to `FindCondition` on every keystroke. Tooltip: `A condition, in the same syntax as a calculated channel — RPM > 4000 && TPS > 80` |
| Summary text | `FindSummary`, 10 pt `Faint`, 10 px left margin, max width 420, ellipsis |
| `◂ Prev` button | padding 8,2, 11 pt, 4 px left margin; enabled when `CanStepFindings` (a search ran and found at least one stretch) |
| `Next ▸` button | same, enabled when `CanStepFindings` |
| `✕` button | padding 6,2, 10 pt, 6 px left margin. Tooltip `Close the find bar` |

#### Opening and closing

| Trigger | Effect |
|---|---|
| Ctrl+F (window-wide) | Only when `ShowLog`: toggle `Finding`. Opening focuses the condition box; closing clears the occurrences. |
| View > "Find in the log…" (enabled when `ShowLog`, shows `Ctrl+F`, tooltip `Jump to where a condition held — RPM > 4000 && TPS > 80`) | If closed, open and focus; if open, just focus the box. |
| `✕` | `Finding = false`, occurrences cleared. |
| Esc in the box | Same as `✕`. |

Closing does not clear the condition, the result or the selection.

#### Running and stepping

- **Enter** in the box: if no result exists for the current condition, run the search; otherwise step forward (Shift+Enter steps back).
- **Prev / Next**: step back / forward.
- Typing in the box (any change to the condition) drops the result: summary blank, Prev/Next disabled. The shaded spans from the previous run stay on the plot until the next run or close.

Run (`MainViewModel.RunFind`):

1. No log, or 0 samples: summary `Open a log first.`; occurrences cleared.
2. Build the filter mask from all **enabled** filters (samples must pass every one). Evaluate the condition at every sample not excluded by the mask (core `LogSearch`). Any non-zero finite result is a match; NaN is "could not be judged". Matching samples are grouped into stretches; a gap of up to 2 non-matching samples inside a stretch does not end it.
3. Parse problem: summary = the parser's message (list under Calculated channels); occurrences cleared; Prev/Next disabled.
4. Otherwise the summary is the parts joined by `   •   ` (three spaces either side):
   - `Nothing matched` when there are no stretches, else `{matches:N0} sample{s} in {stretches:N0} stretch{es}` (`sample` when matches = 1, `stretch` when stretches = 1).
   - `{unknown:N0} could not be judged` when any sample could not be judged.
   - `{excluded:N0} excluded by filters` when any filter is enabled (`excluded = total - passing`).
   - An empty condition gives `Nothing matched` (not an error).
5. If at least one stretch: shade every stretch on the plot (occurrences, from the time of its first sample to the time of its last), then step forward to the first stretch.

Step (`MainViewModel.StepFinding`): no-op when there is no result or it is empty. The index starts at the first stretch (forward) or the last (backward) and then moves by one, wrapping both ways. Summary becomes `Stretch {i:N0} of {n:N0}   •   {matches:N0} samples matched in all` (the unknown/filtered parts are no longer shown). The plot then frames the stretch: `margin = max(2 * (to - from), 1.0)` seconds, `zoom_to(from - margin, to + margin)`, `select_range(from, to)` (so the rows show statistics over the stretch and the "Marked…" hint appears).

Opening another log clears the result (the summary and Prev/Next state); the bar's visibility and condition are kept.

---

### Filters (`FilterItem`)

A filter is a saved condition on one channel: `{name, channel, comparison, low, high, enabled}`. The row model exposes `name`, `description` and an `enabled` toggle; changing `enabled` raises a change that saves all filters and rebuilds the histogram/scatter.

- Description text (core `LogFilter.describe`): `{channel} > {low}`, `{channel} ≥ {low}`, `{channel} < {low}`, `{channel} ≤ {low}`, `{channel} {low}…{high}` (between), `{channel} outside {low}…{high}`. Numbers: whole numbers under 1e9 as integers, otherwise `0.##`, invariant culture.
- Comparison choices (label shown in the editor): `≥  at or above`, `>  above`, `≤  at or below`, `<  below`, `↔  between`, `↮  outside` (two spaces after the symbol). Between and outside need a second value.
- **Filters never change how the plot is drawn.** Filtered-out samples are drawn exactly like any others; filters only decide which samples the histogram/scatter count and which samples the find bar searches.
- Filter panel (shown with Histogram/Scatter, layout owned by the histogram package), for the builder's reference: header `DATA FILTERS` with a `None` button (tooltip `Switch every filter off`) that unticks every filter; note `Samples must meet every ticked condition to be counted.`; each filter row has a tick box, name (11 pt `Text`) and description (10 pt `Faint`); right-click menu `Delete filter` (no confirmation); `+ Add filter` opens an inline editor (channel picker, comparison picker, value box tooltip `Value`, upper box tooltip `Upper value` only when needed, `Add` / `Cancel`). Add errors go to the Hint: `Pick a channel for the filter.`, `Enter a number for the filter value.`, `This comparison needs two values.`; on success the boxes are cleared and the editor closes.
- On opening a log (or the first live snapshot) the saved filters are loaded, then suggestions are added for channels not already covered, **switched off** (e.g. `Engine running`: RPM ≥ 500; `Up to temperature`: coolant).

---

### Channel sidebar

Column 0 when `ShowLog`. Background `Panel`, 1 px right border `Line`. A top block (margin 8 left, 8 top, 8 right, 6 bottom) with everything below in order, and the channel list filling the remaining height with its own scrolling.

#### Top block, in order

1. **Search box**, full width. Tooltip `Filter by channel name, units or category`. Applied 120 ms after typing stops.
2. A row (7 px above): on the right a tick box `Hide unused` (11 pt `Muted`, tooltip `Hide channels that never change value in this log`, **on by default**, not remembered); on the left three sort chips (radio group): `Category` (tooltip `Group channels by what they measure`, default), `A–Z` (tooltip `Flat alphabetical list`), `Plotted` (tooltip `Currently plotted channels first`). Chip style: corner small, 4 px right margin, padding 8,3, 1 px `Line` border, 11 pt `Muted` text; hover text `Text`; checked background `Selected`, border and text `Accent`; hand pointer.
3. A row (7 px above): buttons `Common` (tooltip `Plot the channels most logs are read for`), `All` (tooltip `Plot every channel currently listed`), `None` (tooltip `Clear all traces`), each padding 8,3, 11 pt, 4 px apart; then the summary text, right-aligned, 10 pt `Faint`, ellipsis.
4. **Saved selections** row (8 px above).
5. **Fixed scale editor** (8 px above), only while open.
6. **Calculated channels** block (8 px above).
7. **Preset naming row** (6 px above), only while naming.

#### Channel list filtering, sorting and summary

- A row is listed when:
  - the search text is not empty: name, unit label or category name contains the search text (case-insensitive). The search overrides "Hide unused".
  - otherwise: always, unless "Hide unused" is on **and** the channel is flat (every sample identical) **and** it is not ticked. A ticked channel is always listed.
- Sort:
  - Category: grouped by category display name, groups in this order: `Common`, `Engine`, `Air & boost`, `Fuel`, `Ignition`, `Temperature`, `Idle`, `Electrical`, `Diagnostics`, `Other`; rows by name within a group.
  - A–Z: by name, no groups.
  - Plotted: ticked rows first, then by name, no groups.
  - Name ordering is the culture's string order, ascending.
- Category is the core channel classifier result from name and units (owned by core).
- Summary (`FilterSummary`): empty when there are no rows; otherwise `{listed}/{total} · {plotted} plotted`, plus ` · {hidden} hidden` when `hidden = total - listed > 0`. Recomputed when the search, "Hide unused", sort or ticks change.
- Group headers (Category sort only): a full-width toggle, background `Header`, padding 6,5, 1 px top and bottom border `Line`; a chevron (down when expanded, right when collapsed, 8 pt `Muted`, margin 2 left 8 right), the group name (11 pt semi-bold `Text`) and the row count (11 pt `Faint`, 6 px left). Click collapses or expands the group's rows. Expanded by default; state not remembered. Hover: background `Hover`, chevron `Text`.

#### Channel rows

The list is virtualised (only visible rows are built). Each row: outer padding 6 horizontal, 3 vertical; background transparent, `Hover` under the pointer; inner border corner `CornerSmall`, padding 0,1, background `Selected` while the row is highlighted (its trace is hovered in the plot). List selection has no visual and no meaning.

Columns left to right:

| Element | Details |
|---|---|
| Tick box | Ticks/unticks the channel (plots/unplots it) |
| Colour swatch | 3 x 16 px, corner radius 1.5, margins 6 left 7 right, the channel colour; opacity 0.25 when not ticked. Tooltip = `ScaleNote`: `scale {format(lo)} … {format(hi)}` when a range is pinned, otherwise no tooltip |
| Name column (fills) | Top line: `ƒ` (11 pt `Accent`, 4 px right, tooltip `Calculated channel`) when calculated; the name (default size, `Text`, or `Faint` when the channel is flat, ellipsis); `∿` at the right end (11 pt `Accent`, 4 px left, tooltip `smoothed · median of {n}`) when smoothed. Second line: range text, 10 pt `Faint`, ellipsis |
| Value | Data font, 11 pt, channel colour, 8 px left 2 px right, max width 96, ellipsis |
| Jump buttons | `▲` (tooltip `Jump to this channel's maximum`) and `▼` (tooltip `Jump to this channel's minimum`), 4 px left margin; each 26 x 24, corner small, 1 px horizontal margin, background `PanelAlt`, border `Line`, glyph 12 pt bold `Muted`; hover background and border `Accent`, glyph `OnAccent`; disabled at opacity 0.25. Enabled when `CanJump` = not flat and the channel has a maximum index |

Range text:
- Span statistics present (a span is marked and has data): `{format(min)} … {format(max)}   avg {format(mean)}` (three spaces before `avg`).
- Flat channel: `constant {format(min)}`.
- Otherwise: `{format(min)} … {format(max)}` (whole log).

Value text: `—` initially. On a cursor move: `format_with_units(as-logged value at the cursor)` (`—` for a negative index). When span statistics are present they win: `format_with_units(mean over the span)`, also set at the moment the span is marked; when the span is dropped the value becomes `—`.

Unit system: every row shows in the view model's unit system (`ChannelItem.show(system)`); changing it updates the units label, range text and the cursor value.

#### Row context menu (right-click anywhere on the row)

| Item | Enabled | Action |
|---|---|---|
| `Jump to maximum` | always | Tick the channel, then `focus_time(time of max_index)`. Nothing if the index is < 0 or no log. |
| `Jump to minimum` | always | Same with `min_index`. |
| separator | | |
| `Plot only this channel` | always | Untick every channel (all rows, not just listed ones), then tick this one. |
| separator | | |
| `Colour` ▸ | always | Submenu built each time it opens: one item per colour of the current theme's `Series` palette, each shown as a 26 x 12 swatch with corner radius 2; click pins that colour. Then a separator and `Automatic` (enabled only when the colour is pinned; tooltip `Take whichever palette entry this channel is handed`), which unpins. |
| `Fixed scale…` | always | Opens the fixed scale editor on this row. Tooltip `Draw this channel over a range you choose, in every log` |
| `Smoothing` ▸ | always | Tooltip `Quietens a noisy trace. Only how it is drawn — every measurement still uses the channel as logged`. Items (no check marks): `None` (tooltip `As logged`), `Light` (`Median of 5 samples — takes the fuzz off`), `Medium` (`Median of 15 — a noisy sensor becomes a readable line`), `Strong` (`Median of 51 — the shape only`). |
| `Back to automatic` | always | Tooltip `Take the scheme's colour, the channel's own range and the trace as logged again`. Clears colour pin, range pin and smoothing. |

#### Ticking, colours and bulk actions

- Ticking a row whose colour is not pinned gives it the first palette colour not used by any other ticked row; if all are used, the next palette colour in rotation (a running counter).
- Each tick change (outside a bulk action): re-filter the list if "Hide unused" is on and the channel is flat; update the summary; repaint the plot (keeping the current zoom).
- `Common`: ticks exactly the rows whose category is Common and that are not flat; unticks every other row. One repaint.
- `All`: ticks every **listed** row (current search/filter). One repaint.
- `None`: unticks **every** row, listed or not. One repaint.
- Re-colouring (theme change, unpinning a colour, `Back to automatic`): palette entries held by pinned rows are skipped (unless that leaves nothing, then the full palette is used); unpinned ticked rows get entries in order, then unpinned unticked rows continue the sequence.
- On opening a file: rows in file order (non-time channels), then calculated channels; initial colours handed out in that order; saved styles (colour, range, smoothing, by channel name) applied; then `RPM, MAP, TPS, AFR, CLT` ticked where present and not flat; if none got ticked, the first 4 non-flat rows are ticked.

#### Pinned colour, scale and smoothing: messages

All are remembered by channel name (in every later log), store limit 500 channels.

| Action | Hint |
|---|---|
| Pin a colour | `{name} is pinned to this colour in every log` |
| Automatic colour | `{name} takes the scheme's palette again` |
| Colour store full | `There is no room left to remember another channel's colour. Clear one that is pinned and try again.` (nothing changes) |
| Smoothing None | `{name} is drawn as logged again` |
| Smoothing Light/Medium/Strong | `{name} is drawn smoothed — a median of {5/15/51} samples. Measurements still use it as logged.` |
| Smoothing store full | `There is no room left to remember another channel's settings. Clear one that is pinned and try again.` |
| Back to automatic | `{name} is back to the scheme's colour, its own range and as logged` |

Smoothing is a moving median over the sample count (window shrinks at the ends; a NaN sample stays NaN; NaNs inside a window are skipped). It affects only the trace and the hover card `now` value. Range text, sidebar values, span statistics, max/min, histogram, insights and exports use the channel as logged. The smoothed copy is computed once per row and dropped when the smoothing level changes or the row is re-pointed at new data.

#### Fixed scale editor

Shown while `EditingStyle` (a row is being edited). A box: background `Bg`, corner `Corner`, padding 8, 1 px `Line` border. Contents:

1. `Fixed scale for {name}` — 11 pt `Text`, ellipsis.
2. `Drawn over this range in every log, so the same channel keeps the same shape.` — 10 pt `Faint`, wrapping.
3. `In {units}` — 10 pt `Muted`, only when the row has a unit label.
4. Two equal boxes side by side, 3 px apart: bottom (tooltip `Bottom of the scale`) and top (tooltip `Top of the scale`).
5. `Clear both to go back to the channel's own range.` — 10 pt `Faint`, wrapping.
6. Buttons `Apply` and `Cancel` (padding 10,3, 11 pt, 5 px apart).

Rules:
- Opening seeds the boxes with the range the trace is drawn over right now (`scale(hold_steady)`), converted to the row's display units and rounded to 3 decimals, invariant culture (e.g. `0`, `7999.5`). Opening on another row re-seeds for that row.
- `Apply`:
  - Both boxes blank (whitespace only): unpin; Hint `{name} is scaled to its own range again`; close.
  - Either box not a number (invariant culture float): Hint `A pinned scale needs two numbers, or neither to go back to automatic.`; stays open.
  - Convert both back to the log's own units. Not finite, or top <= bottom: Hint `A pinned scale needs a low value below a high one.`; stays open.
  - Store full: Hint `There is no room left to remember another channel's scale. Clear one that is pinned and try again.`; stays open.
  - Success: pin; Hint `{name} is drawn over {format(lo)} … {format(hi)} in every log`; repaint; close.
- `Cancel`: close, nothing changed.
- Closes by itself when its row is no longer in the channel list (another log opened, or a calculated channel rebuilt).
- Enter/Esc in the boxes do nothing special.

#### Saved selections (presets)

- A row: chips wrapping on the left; a `+ Save` button on the right (padding 8,3, 11 pt, top-aligned, 4 px left; tooltip `Save the channels currently plotted as a named preset`).
- Chips, one per preset, sorted by name (case-insensitive). Chip style: pill corners, margin 0 right-4 bottom-4, padding 9,3, background `PanelAlt`, 1 px `Line` border, 11 pt `Text`, max text width 150 with ellipsis; hover background `Hover`, border and text `Accent`; hand pointer. Tooltip: the channel names joined by `, `; with more than 6, the first 6 then `  +{n - 6} more` (two spaces).
- **Click a chip**: plot exactly the channels the preset names (case-insensitive). If none of them is in this log: Hint `“{name}” names no channel in this log, so nothing changed.` and nothing changes. Otherwise untick all, tick the matches, one repaint; Hint `Applied “{name}” — {n} channels.` or, when some are missing, `Applied “{name}” — {found} of {total} channels; {missing} not in this log.` (curly quotes as shown).
- **Chip right-click menu**: `Overwrite with current selection` (saves the currently ticked channels under this preset's name, no confirmation), separator, `Delete preset` (no confirmation; Hint `Deleted preset “{name}”.`).
- **`+ Save`**: shows the naming row below the calculated-channels block, empties the box and focuses it. Naming row: name box filling the width (max 40 characters, tooltip `Name this selection, then press Enter`), then `Save` and `Cancel` buttons (padding 8,3, 11 pt, 4 px apart).
  - Enter or `Save`: save. Esc or `Cancel`: hide the row and restore the default Hint.
  - Save: no ticked channel: Hint `Plot some channels first, then save them as a preset.`, row stays. The name is trimmed, inner whitespace collapsed to single spaces, cut to 40 characters. Empty name: error (see Open questions for the exact text). A name matching an existing preset (case-insensitive) replaces it. More than 100 presets: `There is a limit of 100 presets.` in the Hint, row stays. Success: Hint `Saved preset “{name}” — {n} channels.` (or `Updated preset …` when replacing), row hides, chips refresh.
- Presets are stored across runs and apply to any log by channel name.

#### Calculated channels

Definitions are stored across runs (limit 60) and applied to every log opened; built channels behave like any other row (plottable, histogram axes, filters) and are marked `ƒ`.

Layout, top to bottom:
1. Chips, one per definition (only when there are any), same chip style as presets, label = name, tooltip = the expression. Click: edit. Right-click menu: `Edit`, separator, `Delete calculated channel`.
2. Problems text (only when there are problems): `{name}: {reason}` for each definition that could not be built on the open log, joined by `   •   `; 10 pt `Marker`, wrapping, 2 px above.
3. Button `ƒ  Add calculated channel` (two spaces; padding 8,3, 11 pt, left-aligned, 4 px above; tooltip `Define a channel from the ones the log already has`). Opens the editor.
4. Editor (hidden until opened; 6 px above): box background `Bg`, corner `Corner`, padding 8, 1 px `Line` border.
   - Row: name box (fills; tooltip `Name, e.g. AFR Error`), 6 px gap, units box 70 px wide (tooltip `Units, e.g. AFR`); 5 px below.
   - Expression box (data font, single line; tooltip `Channel names may contain spaces and need no quoting`).
   - Preview text, 10 pt `Faint`, wrapping, 4 px above, updated as the name or expression is typed.
   - Buttons `Add` and `Cancel` (padding 10,3, 11 pt, 5 px apart, 7 px above). The button says `Add` when editing too.

Preview text (`MathPreview`):
- Expression blank: `Type an expression, e.g. AFR - AFR Target 1`
- No log open: `Open a log to check this expression.`
- Parse error: the parser's message (below). Names offered to the parser are all current row names plus the time channel's name.
- Builds but evaluation gives nothing: `Reads: {referenced names joined by ", "}`
- Constant result: `Constant {format(min)}`; otherwise `Ranges {format(min)} … {format(max)}` (2 decimals, no units).

Actions:
- **Add**: name and expression are trimmed. If either is blank, nothing is saved, no message, and the editor closes (see Open questions). Otherwise the definition is saved with 2 decimals. Errors from the store open a message box (title `OpenLogViewer`, OK, warning icon) and the editor stays open with the text: `There is a limit of 60 calculated channels.` or `A calculated channel called "{name}" already exists.` On success the boxes are cleared, calculated rows are rebuilt over the open log (ticked ones stay ticked, a re-added edited channel is re-ticked if it was ticked before the edit), editor closes, Hint `Added the calculated channel "{name}".` An expression that does not parse is still saved and then appears in the problems text.
- **Edit** (chip click or menu `Edit`): load name, units and expression into the editor, **remove the definition from the store** and rebuild (its row disappears until saved again), remember whether it was ticked; open the editor.
- **Cancel**: clear the boxes; if an edit was in progress, put the original definition back, rebuild, and re-tick it if it was ticked; close.
- **Delete calculated channel**: no confirmation; remove, rebuild, Hint `Removed "{name}".`
- Rebuilding keeps the zoom, selection and other rows; it re-runs histogram axis seeding.

Build problems (reasons in the problems text): `The channel needs a name.`, `This log already has a channel with that name.`, a parser message, or `The expression could not be read.`. Definitions are applied in order and each built channel can be used by later ones. A division by zero or other non-finite result is a missing sample (pen lifts there).

Expression syntax (shared with the find bar; implemented in core):
- Channel names are matched against the log's names, longest first, case-insensitive, to a word boundary, so names with spaces need no quotes. The time channel's name is also available.
- Numbers: digits and `.`, optional exponent (`1e-3`). Constants `pi`, `e`.
- Operators, lowest to highest precedence: `c ? a : b`; `||`; `&&`; `|` (bitwise on integers); `&` (bitwise); comparisons `<= >= == != < >` (not chainable; result 1 or 0); `+ -`; `* / %`; `^` (power, right-associative); unary `- ! +`; brackets.
- Functions (argument counts): `abs sqrt floor ceil log log10 exp sign` (1), `round` (1 to 2), `min max` (2 to 8), `pow` (2), `clamp` (3), `if(cond, a, b)` (3; only the taken branch is evaluated).
- A NaN input gives NaN, including through comparisons.
- Parser messages (exact): `The expression is empty.`; `Unexpected '{c}'.`; `A '?' needs a matching ':'.`; `The expression ends early.`; `Missing ')'.`; `'{token}' is not a number.`; `'{word}' is not a channel in this log, nor a function.`; `'{word}' needs its arguments in brackets.`; `'{name}' takes {n} argument{s}, not {count}.` (`{n}` is `1` or `{min} to {max}`; the plural `s` is left off only when the maximum is 1); `The expression nests more than 64 levels deep.`

---

### Toolbar and menu items for this view

| Control | Where | Rule |
|---|---|---|
| Chips `Overlaid` / `Stacked` | toolbar, visible only when `ShowLog` | Tooltips: `All traces share the plot — best for comparing timing between channels` / `Each channel gets its own strip — best when several are plotted`. Changing repaints at the current zoom. |
| View > `Overlaid traces` / `Stacked traces` | menu, checkable, enabled when `ShowLog` | Same as the chips, same tooltips. |
| View > `Find in the log…` | enabled when `ShowLog` | See Find bar. |
| View > `Reset zoom` | enabled when `ShowLog` | See View operations. |
| View > `Draw steady channels as steady` | checkable, on | See Vertical scale. |
| Status bar cursor text | right of the status strip, data font 11 pt `Accent`, 14 px left margin | `CursorTime`; `—` initially. |
| Ctrl+O | window-wide | Open a datalog (dialog title `Open datalog`). Also drag-and-drop of a file onto the window (a `.msq` loads a tune instead). |

Opening a file: `MainViewModel.Load`, then `set_document`. Failure (format, I/O, access): message box, title `OpenLogViewer`, OK, warning icon, text `Could not open {file name}.\n\n{error message}`. Success: window title `{file name} — OpenLogViewer`; Status `{format name}   •   {n} channels   •   {samples:N0} samples   •   {duration:F1} s` plus `   •   {n} markers` when there are markers and `   •   {signature}` when there is one. Initial Status: `Ready — open a .mlg, .msl or .csv datalog`.

### Command-line switches (scripted runs)

| Switch | Effect |
|---|---|
| `--stacked` | Turn stacked lanes on. |
| `--select a,b` | After loading, `select_range(a, b)` in seconds. Both numbers must parse (current culture, see Open questions). |
| `--find "condition"` | Open the find bar, set the condition and run it (frames the first stretch). |
| `--pointer fx,fy` | Only for a screenshot capture: just before rendering, place the cursor as if the pointer were at `(fx * plot width, fy * plot height)`. The fractions are of the **whole plot control**, not the plot area. Shows the hover card. |
| `--cell c,r` | Rebuild the histogram and trace cell (c, r) back to the log: switch to the plot, shade every visit, frame the longest visit with margin `max(2 * length, 1.0)` s, and select it. The cell-trace Hint is owned by the histogram package. |

---

### Open questions

1. **Cursor snapping is "at or before", not "nearest".** `index_at(t)` returns the last sample with time <= t, while the code comment says "nearest sample". Builder can settle: keep the floor behaviour to match the C# exactly.
2. **Live follow vs programmatic moves.** `focus_time` (jump to max/min) and `zoom_to` (find stepping, cell tracing) do not stop follow, so on a live session the next 200 ms tick slides the view back to the newest 30 s and the jump is lost. A plain click in the plot does stop follow (pan starts on press). Decision for the user: should jumps/find stop follow? Builder default: match the C#.
3. **Double-click on a live session** stops following and frames the whole session, while View > Reset zoom on a live session resumes following. Inconsistent; user decision.
4. **Connecting live while a log file is open.** The row list is not cleared and `set_document` is never called: rows are re-pointed by name, rows with no matching live channel keep pointing at the old file's data, calculated channels from the file stay, and the old selection/occurrences/view stay on the plot drawn against the new time base. Looks like a bug; user decision (likely: treat a new live session like opening a file).
5. **Calculated channels are not built for live sessions** and the problems text from the previous file stays. User decision.
6. **Leaving the plot keeps stale values.** On pointer leave the cursor disappears, but the rows' values and the status-bar cursor text keep showing the last sample. Builder can settle (match C#), or the user may want them cleared.
7. **Smoothed vs raw readouts disagree.** The hover card's `now` uses the smoothed value; the row value next to it uses the as-logged value. Possibly intended ("measurements use as logged"), but the two readouts at one cursor differ. User decision.
8. **Pinned ranges are not clipped to their lane.** In stacked mode a value outside a pinned range is drawn into neighbouring lanes (and outside the area in overlaid mode). Builder can settle: matching the C# means no clipping except at the control edge.
9. **Hover card at a NaN sample.** In stacked mode the hovered channel can have NaN at the cursor; the card's y becomes NaN (WPF then draws nothing visible). Builder can settle: hide the card or anchor it to the lane centre.
10. **Clicking the plot clears find shading.** A plain click clears the selection and with it the occurrences (find stretches, traced cell visits), though the find bar stays open with its summary. Editing the condition clears the summary but leaves the shading. Builder can settle (match C#); user may prefer otherwise.
11. **Add calculated channel with a blank name or expression** silently closes the editor and discards what was typed, with no message. When editing, that also loses the original definition (it was removed on Edit and Cancel is no longer possible). Looks like a bug; user decision.
12. **Editing a second calculated channel while one edit is open** overwrites the first edit's state; the first definition was already removed from the store and is lost. Bug; user decision.
13. **Empty preset name message.** The store throws an argument error whose .NET message is `A preset needs a name. (Parameter 'name')`, which is put in the Hint as-is. Builder can settle: probably show `A preset needs a name.` only.
14. **Number culture.** Plot labels (`F2`, `F3`, `N0`), row values (`F{digits}`), hints and summaries are formatted with the **current culture** in the C#, while the time-axis seconds labels, fixed-scale boxes and expression numbers are invariant. Filter values (`Enter a number…`) and `--select`/`--pointer` arguments are parsed with the current culture, fixed-scale boxes with invariant. Builder can settle: the Rust uses a .NET-compatible formatter; choose one culture policy and apply it consistently (recommended: invariant everywhere, which is identical on an English system).
15. **Plotted sort does not re-sort on a single tick.** In "Plotted" sort, ticking one row does not move it until something refreshes the list (search, bulk action, sort change). Builder can settle (re-sorting live is probably what users expect).
16. **Ctrl+F while in Gauges/Calibration mode** toggles the hidden find bar (because `ShowLog` stays true in other modes). Builder can settle: only when `InLogMode && ShowLog`.
17. **Overwrite preset / delete preset / delete calculated channel have no confirmation.** Probably intended; user decision if not.

### Safety rules found

None in this package. The plot, sidebar, presets, styles, calculated channels and find bar only read the open log or live snapshot and write local settings files (presets, styles, calculated channels, filters). Nothing here writes to an ECU, burns, connects or restores. Smoothing is display-only by rule: nothing that judges the engine (insights, VE calibration, histogram, statistics, exports) may read a smoothed value.


## §3 `w7-analysis-views`: Histogram (heat table) and Scatter

### Scope

The two "derived" readings of a recording in Log mode: the **Histogram** (a heat table shaped like an ECU tuning table) and the **Scatter** (every sample at its own X/Y, coloured by a third channel), plus the **axis settings panel** that replaces the channel list while either is showing, VE Calibration, comparison against a second log, trace-back from a cell/mark into the plot, and the exports these modes offer.

Rust module: `olv::analysis_views`. The binning maths (`HistogramTable`, `ScatterPlot`/`ScatterBins`, `VeAnalysis`, `WidebandDelay`, `LogComparison`, `CsvExport`, `SampleFilter`/`LogFilter`) lives in the core library; this section says what the UI asks of it and what it shows. Where a core rule changes what the user sees, it is stated here.

Status-line text below means `MainViewModel.Hint` (the single status line owned by another section). "Report" means: set the status line AND print the same text to the scripted-run output (`App.Report`).

---

### Where these modes live

- In Log mode (`InLogMode`) the window is three columns: **left column 300 px wide (min 200)**, a **4 px splitter** (colour `Line`, draggable), and the **view area** (rest, min 300).
- The log view choice is `MainViewModel.LogView` ∈ {Plot, Histogram, Scatter}. Picked by:
  - toolbar chip group (visible only in Log mode): `Log` (tooltip `Time-series view`), `Histogram` (tooltip `Bin the log into a table, like a tuning table`), `Scatter` (tooltip `Every sample at its own X and Y, coloured by a third channel`);
  - View menu check items `_Plot`, `_Histogram`, `Sc_atter` (same tooltips; enabled only in Log mode). Checking one selects it; unchecking does nothing (the three are exclusive, one always on).
- When LogView is Histogram or Scatter (`ShowAxisPanel`), the left column shows the **axis settings panel** instead of the channel list, and the view area shows the HistogramView or ScatterView respectively (the plot and its find bar are hidden).
- Changing LogView always triggers a rebuild (see "Rebuild rule").
- Mouse cursor: ScatterView shows a crosshair cursor; HistogramView the normal arrow.

---

### Axis settings panel (left column, Histogram and Scatter)

Background `Panel`, 1 px right border `Line`. Vertical scroll when it overflows; no horizontal scroll. Inner margin left 10, top 10, right 12, bottom 12. Controls stack top to bottom:

| # | Control | Shown when | Behaviour |
|---|---|---|---|
| 1 | Heading `AXIS FIELDS` (10 pt SemiBold, `Muted`, 6 px below) | always | — |
| 2 | Label `X axis` (11 pt `Muted`) + dropdown | always | Items: `AxisChannels` (by channel name, ellipsis-trimmed). Selecting sets `XAxis` → rebuild. Margin below 7. |
| 3 | Label `Y axis` + dropdown | always | Same list; sets `YAxis` → rebuild. |
| 4 | Label `ZAxisCaption` + dropdown | always | Caption is `Z axis — the colour of each mark` in Scatter, `Z axis — the value in each cell` in Histogram. Same list; sets `ZAxis` → rebuild. |
| 5 | Label `Compare against — shows the difference` + dropdown | always | Items: `CompareOptions` shown by label. Tooltip: `Subtract a target channel, so each cell shows how far off it is rather than what it read`. Sets `ZCompare` → rebuild. Margin below 12. |
| 6 | Histogram-only block (rows 6a–6k) | LogView = Histogram only (hidden, not disabled, in Scatter) | see below |
| 7 | Heading `DISPLAY` | always | — |
| 8 | Check `Colour by sample count` (11 pt) | always | Tooltip `Shade cells by how much data backs them, rather than by value`. Sets `ColorByCount` → rebuild. |
| 9 | Check `Only the zoomed time range` (11 pt, 12 below) | always | Tooltip `Build the table from the range zoomed to in the log view`. Sets `HistogramZoomOnly` → rebuild. Applies to Scatter too. |
| 10 | Heading `DATA FILTERS` with a right-aligned button `None` (padding 7,2, 10 pt) | always | Tooltip `Switch every filter off`. Unticks every filter (each untick saves and rebuilds). |
| 11 | Note `Samples must meet every ticked condition to be counted.` (10 pt `Faint`, wraps) | always | — |
| 12 | Filter list | always | One row per filter: a checkbox (left, 7 px gap) then the filter name (11 pt `Text`, ellipsis) above its description (10 pt `Faint`, ellipsis). Ticking/unticking saves the filter set and rebuilds. Right-click a row → context menu with one item `Delete filter` (always enabled) → removes it, saves, rebuilds. |
| 13 | Button `+ Add filter` (padding 8,3, 11 pt, left-aligned, 7 above) | always | Opens the filter editor (row 14). |
| 14 | Filter editor box (hidden until opened; background `Bg`, 1 px `Line` border, theme corner radius, padding 8, 7 above) | after `+ Add filter` | Channel dropdown (`AxisChannels`, preselected to the X axis channel when a log is opened); comparison dropdown (labels below); a value box (tooltip `Value`) and, only for "between"/"outside", a second box (tooltip `Upper value`); buttons `Add` (padding 10,3) and `Cancel`. `Add` validates (messages below); on success the filter is added **enabled**, saved, the two value boxes are cleared, the editor closes, rebuild. `Cancel` just closes (fields keep their text). |

Comparison dropdown labels, in order: `≥  at or above`, `>  above`, `≤  at or below`, `<  below`, `↔  between`, `↮  outside` (two spaces after the symbol). Default: the first.

Filter description text: `{Channel} > {low}`, `{Channel} ≥ {low}`, `{Channel} < {low}`, `{Channel} ≤ {low}`, `{Channel} {low}…{high}`, `{Channel} outside {low}…{high}`. Numbers: whole numbers (abs < 1e9) as integers, others `0.##`, invariant culture.

`Add` validation (status line, editor stays open):
- no channel: `Pick a channel for the filter.`
- value not a number (current-culture parse): `Enter a number for the filter value.`
- between/outside and second value not a number: `This comparison needs two values.`

Filters persist across logs (a filter store). On opening a log, saved filters are loaded, then suggested filters are appended for any channel not already covered, **switched off**: `Engine running` (RPM ≥ 500), `Up to temperature` (coolant ≥ 70, or ≥ 160 when units contain F or max > 130), `Off idle` (throttle > 1), `Not cutting fuel` (fuel-cut ≤ 0), `AFR in range` (9…20) or `Lambda in range` (0.6…1.4). Each suggestion is offered only when that role's channel exists and is not flat.

#### Histogram-only block (row 6)

| # | Control | Shown when | Behaviour |
|---|---|---|---|
| 6a | Heading `CELL VALUE` (5 below) | Histogram | — |
| 6b | Chip group `Mean` `Min` `Max` `Count` (exclusive; 12 below) | Histogram | Sets `Statistic` → rebuild. Default Mean. |
| 6c | Heading `AXIS BREAKPOINTS` with right-aligned button `Open tune…` (padding 7,2, 10 pt) | Histogram | Tooltip `Use tables from a .msq file instead of the one stored in the log`. Opens the tune file flow (same as File > Open tune…). |
| 6d | `Tune: {TuneSource}` (10 pt `Faint`, ellipsis) with a right-aligned `✕` button (padding 5,0, 10 pt) | `✕` only when `UsingLoadedTune` | `✕` tooltip `Go back to the tune stored in the log`; calls ClearTune then rebuild. |
| 6e | `TuneWarning` text (10 pt `Marker`, wraps, 4 below) | when `TuneWarning` non-empty | — |
| 6f | Axis source dropdown (items `AxisSources` by label) | Histogram | Tooltip `Bin onto the tune's own table axes, so cells line up with the table you are editing`. Sets `AxisSource` (rules below) → rebuild. |
| 6g | Note `No tune available, so bins come from the data. Open a .msq to use its tables.` (10 pt `Faint`, wraps, 8 below) | when `HasTuneAxes` is false (only "From the log data" offered) | — |
| 6h | Heading `TABLE SIZE`, then two equal columns: `Columns` box and `Rows` box (labels 11 pt `Muted`) | only when `UsingDataAxes` (axis source is "From the log data") | Integer. Applied 400 ms after typing stops. Clamped to 2…40. Non-integer text is not applied. Defaults 16 × 16. |
| 6i | Heading `VE CALIBRATION` | Histogram | — |
| 6j | Check `Suggest a new fuel table` (11 pt) | Histogram; **enabled** when `VeAvailable` | Tooltip `Compare logged AFR against the target and scale the tune's cells to match`. Sets `VeAnalyze` → rebuild. |
| 6k | VE options (see "VE Calibration") | only while `VeAnalyze` is on | — |

`TuneSource` text: the opened file's name when one is loaded; `from the log` when the log embeds a tune; `{format}, unreadable` when the log carries a tune in a format that cannot be read; otherwise `none`.

`AxisSources` list, in order:
1. `From the log data` (always first; the default after every log open or tune change).
2. One entry per table axis set in the current tune (opened file if any, else the log's embedded tune), label `{Name}  ({cols}×{rows})` (two spaces, `×` U+00D7). Carries the table's values when they could be read.
3. Tables read off a connected ECU whose X and Y channels this log carries, label `{Name}  ({cols}×{rows}) — from the ECU`, sorted: labels containing `VE ` first, then alphabetical (case-insensitive). Each knows the log channel names the firmware indexes it by.

Selecting an axis source with axes:
- X axis becomes the firmware's X channel when named and present in `AxisChannels`; else keep the current X if its units equal the axis units or `rpm` (case-insensitive); else the first channel with the axis units; else the first with `rpm`; else unchanged. Y the same with fallback `kpa`.
- The Table Size inputs hide; the table is binned on the tune's breakpoints.

On opening a log (and on every tune change with a log open), the panel is re-seeded:
- `AxisChannels` = channels that are not flat, plus flat ones an ECU table is indexed by; sorted by name (case-insensitive).
- X = channel named `RPM` (case-insensitive) else first; Y = `MAP` else `Load` else second; Z = `AFR` else `Lambda` else third.
- `CompareOptions` = `None — show the value itself` then **every** channel (flat ones included), by name.
- Compare reset to None; axis source reset to `From the log data`; VE Calibration switched **off**; find results cleared.

Panel with no log: dropdowns are empty; views show their "Choose channels…" message.

#### Tune file flow (Open tune… / ✕)

- Open-file dialog title `Open tune`, filter `TunerStudio tune|*.msq|All files|*.*`, must exist.
- A file with no readable table axis sets fails with: `No usable tables found in that file. It should be a TunerStudio .msq tune.`
- Read failures show a message box: title `OpenLogViewer`, text `Could not read {fileName}.\n\n{error}`, OK only, warning icon.
- On success: if the log has an embedded tune and both have a table named `VE table 1` whose values differ (any cell by > 1e-6, or different size), `TuneWarning` = `This tune's fuel table differs from the one stored in the log. VE Calibration scales the numbers that produced the logged AFR, so suggestions will be off by however much the tune has changed since the drive.` and the status line shows that warning; otherwise status `Using tables from {fileName}.`
- `✕` / File > `Use the tune stored in the log` (enabled when `UsingLoadedTune`): status `Back to the tune stored in the log.`, warning cleared.
- Both re-seed the panel and rebuild.

The toolbar tune indicator (`Tune: {TuneSource}`, max 200 px, ellipsis, tooltip `TuneDetail`; colour `Faint`, `Accent` when a file is loaded, `Marker` when `TuneWarning` is set — warning wins) belongs to the toolbar section; its tooltip texts are:
- loaded: `Tables come from {file}. Right-click to go back to the log's own tune.`
- embedded: `Tables come from the tune stored in this log — the one that was running when it was recorded.`
- unreadable: `This log carries its {format} tune, in a format this cannot read. Axis breakpoints and VE Calibration need a tune it can, so both are unavailable for this log.`
- none: `This log carries no tune. Open a .msq to bin onto its table axes and to use VE Calibration.`

---

### Rebuild rule

A rebuild runs whenever any of these change: LogView, X/Y/Z, Compare, columns/rows, statistic, colour-by-count, zoomed-range-only, axis source, any filter (add/delete/tick), the tune, VE on/off and its four settings, the comparison log (open/stop/show difference). Also after `Find it`, after the tune flow, and, during a live session, on every 5th live refresh tick while LogView is not Plot.

Rebuild:
1. No document or 0 samples → both views get "nothing" (they show "Choose channels…").
2. Sample window = whole log, unless `Only the zoomed time range` is on AND the plot has a non-empty view (end > start): first = sample index at the plot's view start time, last = index at the view end time.
3. If LogView is Scatter → build the scatter only. Otherwise (Histogram **or Plot**) → build the table and its visited-cell rings. Only one is built per rebuild.

**Open question:** the table is rebuilt even when the Plot is on screen (anything not Scatter builds the table). Harmless but contradicts "only the one on screen is built". Builder can settle (matching C# is safe).

---

### HistogramView (the heat table)

Fixed layout constants (device-independent px): left gutter **66**, bottom gutter **32**, top gutter **44**, right pad **14**, gap between cells **1.5**. Clipped to its bounds. Font: theme UI font, normal weight.

Colours: ground = theme `Background`; axis text and legend text = `Muted`; title and hover text = `Text`; empty cell = `EmptyCell`; hover ring = `Text` 1.5 px; selected (traced) cell ring = `Marker` 2 px; visited cell rings = `Accent` 1.5 px. Cell text ink is `#0B0B0B` when the fill's WCAG luminance > 0.4, else `#FFFFFF`.

#### States (checked in this order)

| Condition | Shown (13 pt `Muted`, centred in the whole view) |
|---|---|
| no table (no log, or X/Y/Z not chosen) | `Choose channels to build a table` |
| table has no populated cell | `No samples in this range` |
| cell width < 6 or cell height < 6 | `Not enough room — reduce rows or columns` |
| otherwise | the table |

Cell width = (viewWidth − 66 − 14) / columns; cell height = (viewHeight − 44 − 32) / rows.

#### Draw order

1. Fill whole view with `Background`.
2. Title, at (66, 10), 13 pt `Text`: `{X.Name}  ×  {Y.Name}   —   {what}` (two spaces each side of `×`, three each side of `—`). `{what}` = the table's display name when it has one (VE and difference tables), else `{statistic} {measure}` with statistic `mean` / `min` / `max` / `count of`, measure = `{Z.Name}` or, with a compare channel, `{Z.Name} − {Compare.Name}` (U+2212 minus).
3. Legend (top right; see Colour scales).
4. Cells, column by column. Cell (c, r) rectangle: x = 66 + c·cw + 0.75, y = 44 + (rows − 1 − r)·ch + 0.75, w = max(1, cw − 1.5), h = max(1, ch − 1.5). **Row 0 (lowest Y) is at the bottom.** No value → `EmptyCell` fill. Value → scale colour fill, then, only when cw ≥ 34 and ch ≥ 15, the formatted value in 11 pt, centred, skipped if its width > cell width − 4.
5. Visited rings: for every cell the marked plot span reached, an `Accent` 1.5 px outline inset 1 px inside the cell (no inset if the cell is ≤ 2 px).
6. Axes (10 pt `Muted`):
   - Column labels every `xStride` columns, xStride = max(1, ceil(42 / cw)), starting at column 0; text = column centre; centred horizontally on the column, top at y = 44 + rows·ch + 6.
   - Row labels every `yStride` rows, yStride = max(1, ceil(15 / ch)), from row 0; right edge 7 px left of x = 66, centred vertically on the row.
   - X channel name centred under the grid, bottom at view height − 2. **No Y-axis name is drawn** (Y is named only in the title).
   - Axis number format: |v| ≥ 100 → `N0` (current culture, thousands separators); else `0.#`.
7. Selected (traced) cell: `Marker` 2 px outline on the cell rectangle, when its column/row are inside the table.
8. Hover: `Text` 1.5 px outline on the hovered cell, plus a readout at (66, view height − textHeight − 2), 11 pt `Text`:
   - no samples: `{X.Name} {colCentre} · {Y.Name} {rowCentre} — no samples`
   - otherwise: `{X.Name} {colCentre} · {Y.Name} {rowCentre} — {Z.Name} {cellText} from {count:N0} sample` + `s` unless count = 1.
   Centres use the axis format above. `·` is U+00B7.

**Open question:** the hover readout and the X channel name are both drawn at the bottom-left/bottom-centre baseline (view height − 2) and overlap when both are present. Builder can settle (e.g. keep identical positions to match C#, or nudge).

**Open question:** in VE and difference tables the readout still says `{Z.Name}` (e.g. `AFR +3.2`) although the cell holds a percentage change or a VE number. Spec ambiguity; matching C# is the default.

#### Cell value formatting (`HistogramTable.Format`)

- Count statistic: integer, `N0`.
- Table with a display-digit count (VE, difference): `F{digits}` (digits clamped 0…6), current culture.
- Otherwise the Z channel's own format: value (converted to the active unit system) with `F{Z.Digits}`; NaN shows `—`.
- When the table has a compare channel (delta) and the value > 0, prefix `+`. Negative values carry the normal `-`.
- Null cell → empty text.

#### Hover and click

- Mouse move: column = trunc((x − 66) / cw); row = rows − 1 − trunc((y − 44) / ch). Pointer left of x = 66, above y = 44, or outside 0…cols−1 / 0…rows−1 → no hover. Redraw only when the hovered cell changes. Mouse leave clears hover. New table clears hover (not the selected ring).
- Left button down: if a cell is hovered and its sample count > 0 → trace it back (below). Clicking an empty (count 0) cell does nothing. A click without a preceding mouse move over the view hits nothing.
- No right-click menu, no wheel, no drag, no zoom/pan, no keyboard handling, no tooltip.

**Open question:** the selected (`Marker`) ring is never cleared. After a trace, changing axes, size, statistic or log keeps the ring at the same column/row index on the new table. Likely a bug; builder can settle (clearing it on any table change is the obvious fix) — or user decides.

#### Trace a cell back to the log

On a populated-cell click (`OnHistogramCellActivated`):
1. Collect the cell's samples: every sample in the table's window that passes the table's filters, with X, Y, Z (and compare channel, if any) not NaN, whose nearest column centre and nearest row centre are this cell.
2. Group into visits: consecutive samples whose indices differ by ≤ 3 belong to one visit. No visits → stop.
3. Longest visit = largest (last − first); first one wins ties.
4. Mark this cell as the selected cell (ring).
5. **Switch LogView to Plot.**
6. Give the plot every visit as an "occurrence" (time from → to) — the plot's occurrence marking is owned by the plot section.
7. Zoom the plot to [from − m, to + m], m = max((to − from) × 2, 1.0) s, where from/to are the longest visit's times; then select (mark) the span from → to.
8. Status line: `{X.Name} {colCentre:G6} · {Y.Name} {rowCentre:G6} — {cellText} from {samples:N0} samples over {visits}. Showing the longest ({longestLength:N0} samples).` where `{visits}` = `one visit` or `{n} visits, all marked`; samples = sum of visit lengths; longestLength = last − first + 1.

Consequence (document, do not "fix" silently): if `Only the zoomed time range` is on, returning to the Histogram builds the table from the trace's zoom window. **Open question:** user decision whether trace-back should leave the table window alone.

#### Visited cells (mark a span → see the cells)

After every ordinary (non-VE) non-empty table build, if a span is marked on the plot, compute for every sample in the span its cell (inside the table window, passing filters, X and Y not NaN → nearest centres). Cells reached get rings. Status-line part (see below):
- none reached and some samples outside: `the marked span falls outside this table`
- none reached otherwise: `the marked span reached no cell`
- else `the marked span reached {cells:N0} cell` + `s` unless 1, plus ` ({outside:N0} of its samples fall outside)` when any fall outside.
Rings are cleared on every rebuild and are not computed for VE tables or empty tables. Marking a span in the plot does not itself rebuild; rings appear on the next rebuild (e.g. switching to Histogram).

#### Status line after a table build

- Empty table: `Every sample was filtered out — loosen or switch off a filter.` when filters are applied and none pass; else `No samples fall in this table — try a wider time range.`
- Otherwise parts joined by `   •   ` (three spaces, U+2022, three spaces):
  1. (first, only when a comparison was applied) `difference against {comparisonFileName}`
  2. `{SampleCount:N0} samples across {populated} of {columns×rows} cells`
  3. `axes from {axisSetName}` (when on tune axes)
  4. `{excluded:N0} of {total:N0} excluded by filters` (when any filter is applied)
  5. `not in this log: {names, comma-space separated, distinct}` (filters naming missing channels)
  6. the visited-cells part above.

#### Table binning rules (core, visible to the user)

- From the log data: per axis, min/max over samples in the window that pass the filters (NaN ignored); no data → 0…1; zero span → widened by ±0.5. Step = span / bins; centre i = min + (i + 0.5)·step.
- From tune axes: centres = the table's breakpoints.
- Each sample (passing filters, X/Y/Z not NaN, compare not NaN) goes to the **nearest** centre on each axis (ties to the lower index; out-of-range to the end). With a compare channel, Z − compare is taken per sample before aggregating.
- Cell value: mean / min / max / count of the samples in it.

---

### Colour scales (shared by HistogramView, ScatterView)

Theme inputs (other section owns the tokens): `Background`, `Text`, `RampCool`, `RampWarm`. `IsDark` = WCAG luminance(Background) < 0.35.

**Sequential ramp** (5 stops) = `Ramp(RampCool, IsDark)`:
- Take the anchor's OKLCH (L, C, h); C' = clamp(C, 0.09, 0.20).
- For i = 0…4, t = i/4: L = startL + (endL − startL)·t; C = C'·(1.00 + (0.45 − 1.00)·t); hue = anchor hue.
- Dark: startL 0.50 → endL 0.88. Light: 0.78 → 0.36.
- Convert OKLCH → OKLab (a = C·cos h, b = C·sin h) → linear sRGB (standard OKLab matrices) → clamp 0…1 → sRGB gamma (≤ 0.0031308: ×12.92, else 1.055·c^(1/2.4) − 0.055) → round to byte.

**Diverging arms** (6 stops each): CoolArm from `RampCool`, WarmArm from `RampWarm`:
- stop 0 = Blend(Background, White if dark else Black, 0.18) (the neutral midpoint);
- stops 1…5 = Build(anchor, 5 steps, dark 0.47 → 0.76 / light 0.72 → 0.43, chroma factor 1.00 → 0.85), same procedure as above.

`Blend(a, b, t)` = per channel round(a + (b − a)·t), t clamped 0…1.
`EmptyCell` = Blend(Background, Text, 0.05).

**Sampling** a ramp at t ∈ [0,1]: scaled = t·(n − 1); index = min(floor(scaled), n − 2); f = scaled − index; each channel = round(a + (b − a)·f) between stop[index] and stop[index+1]. Rounding is .NET `Math.Round` (round half to even) — the Rust must use half-to-even to be byte-exact.

**Histogram cell colour:**
- Colour by count: t = 1 if MaxCount ≤ 1, else (count − 1) / (MaxCount − 1), clamped; sequential ramp. (Linear.)
- Else, if the table shows deviation (has a compare channel AND statistic ≠ Count): reach = max(|MinValue|, |MaxValue|); t = 0 if reach ≤ 0, else clamp(|v| / reach, 0, 1); CoolArm when v < 0, WarmArm otherwise (v = 0 → WarmArm at t 0 = neutral).
- Else: span = MaxValue − MinValue; t = 1 if span ≤ 0, else clamp((v − MinValue)/span); sequential ramp.
- Cells with no value are always `EmptyCell`, whatever the mode.

**Scatter mark colour (per block, from the block mean):**
- Colour by count: **logarithmic**: t = 1 if Busiest ≤ 1, else ln(count)/ln(Busiest), clamped; sequential.
- Else if delta (compare channel set): reach = MeanExtent = max(|ColorLow|, |ColorHigh|); t = 0 if reach ≤ 0 else clamp(|mean|/reach); CoolArm if mean < 0 else WarmArm.
- Else: t = 1 if ColorHigh − ColorLow ≤ 0, else clamp((mean − ColorLow)/(ColorHigh − ColorLow)); sequential.
- Trimmed colour range: ColorLow/High start as the min/max of the block means. When occupied blocks × 0.02 ≥ 1 (≥ 50 occupied blocks), they become the 2nd and 98th percentiles of the sorted block means (position = fraction·(n − 1), linear between neighbours); if that closes the range (high ≤ low) the full range is kept. ClipsLow = ColorLow − MeanMin > 5% of (MeanMax − MeanMin); ClipsHigh likewise at the top.

**Legend** (both views, top right): a bar 132 px wide × 9 px high at y = 16, drawn as 132 strips 1.2 px wide at x = barX + i, colour at t = i/131.
- Diverging bar (delta and not colour-by-count): t < 0.5 → CoolArm at 1 − 2t; else WarmArm at (t − 0.5)·2. A `0` label (9 pt `Muted`) centred under the bar's midpoint at y = 16 + 9 + 1.
- Layout right to left: high text right-aligned to view width − 14; bar ends 6 px left of it; low text ends 6 px left of the bar; caption ends 10 px left of the low text. All texts 10 pt `Muted`, vertically at 16 + (9 − textHeight)/2.
- Histogram ends: count mode `1` … `{MaxCount:N0}`; diverging `−{Z.Format(reach)}` … `+{Z.Format(reach)}` (U+2212 on the low end); else `{Format(MinValue)}` … `{Format(MaxValue)}`. Caption `samples` / `vs target` / `{Z.Name}`.
- Scatter ends: count mode `1` … `{Busiest:N0}`; diverging `−{Z.Format(MeanExtent)}` … `+{Z.Format(MeanExtent)}`; else `≤` prefix when ClipsLow + `{Z.Format(ColorLow)}`, `≥` prefix when ClipsHigh + `{Z.Format(ColorHigh)}`. Same captions.

**Open question:** the histogram's diverging legend uses the Z channel's precision, not the table's display digits (VE change table: legend shows AFR precision, cells show 1 decimal). Builder can settle.

---

### ScatterView

Same gutters as the table (left 66, bottom 32, top 44, right 14). Plot area = (66, 44, max(0, W − 80), max(0, H − 76)). Clipped. Pixel-snapped, aliased edges.

#### States (in order)

| Condition | Shown (13 pt `Muted`, centred) |
|---|---|
| no plot (no log, or X/Y/Z not chosen) | `Choose channels to plot` |
| no points | `No samples in this range` |
| plot area width < 40 or height < 40 | `Not enough room` |
| otherwise | the scatter |

#### Points (core)

Every sample in the window: filtered-out samples are counted as Filtered; samples with X, Y or Z NaN (or compare NaN when set) are counted as Dropped; the rest become points (Z − compare when a compare channel is set). XMin/XMax/YMin/YMax are the min/max over the points (no padding).

#### Decimation / binning

The marks are a block grid: columns = max(1, floor(areaWidth / 3)), rows = max(1, floor(areaHeight / 3)). Block of a value: if max ≤ min → count/2 (integer division); else clamp(floor((v − min)/(max − min)·count), 0, count − 1). Per block: count, mean, lowest, highest of Z. The grid is painted as a bitmap of one pixel per block (row 0 at the bottom), stretched to the plot area with **nearest-neighbour** scaling, so each block is ≈3 px square. Blocks with no points are **transparent** (not tinted). The grid and bitmap are cached and thrown away (with the hover) whenever the plot, the theme or the view size changes.

#### Draw order

1. `Background` fill.
2. Title at (66, 10), 13 pt `Text`: `{X.Name}  ×  {Y.Name}   —   {measure}`, measure = `{Z.Name}` or `{Z.Name} − {Compare.Name}`.
3. Legend (as above).
4. Grid lines behind the marks, 1 px, colour `EmptyCell`, at every X tick (vertical, full plot height) and Y tick (horizontal, full width); positions rounded to whole pixels + 0.5.
5. The marks bitmap over the plot area.
6. Axis labels (10 pt `Muted`): X ticks centred at their x, top at area bottom + 6; Y ticks right edge at x = 59, centred on their y; X channel name centred under the area, bottom at view height − 2. No Y-axis name.
7. Hover ring and readout.

Ticks (`Ticks(min, max, target 6)`): if min/max not finite or max ≤ min → [min]. raw = (max − min)/6; magnitude = 10^floor(log10(raw)); step = magnitude × (1 if raw/mag ≤ 1, 2 if ≤ 2, 5 if ≤ 5, else 10). Ticks from ceil(min/step)·step while ≤ max + step·1e-9, adding step each time; a tick with |t| < step·1e-9 is written as 0. Empty → [min]. Position fraction = (v − min)/(max − min), or 0.5 if max ≤ min. Tick format: |v| ≥ 100 → `N0`, else `0.##`.

#### Hover

- Mouse move: no hover if no grid or the point is outside the plot area. Else column = trunc((x − areaX)/areaW·cols), row = trunc((areaBottom − y)/areaH·rows), both clamped. If that block has points, it is the hover. Otherwise search the 5×5 neighbourhood (radius 2; outer loop row offset −2…+2, inner column offset −2…+2) for the block with the **most** points (strictly greater wins, so the first found keeps ties); none → the empty block under the pointer. Redraw only on change; mouse leave clears it.
- Ring: `Text` 1.5 px, rectangle (areaX + col·bw − 2, areaBottom − (row + 1)·bh − 2, bw + 4, bh + 4), bw = areaW/cols, bh = areaH/rows. Skipped if the index is outside the current grid.
- Readout at (areaX, view height − 16), 11 pt `Text`. Block centre x = XMin + (col + 0.5)/cols·(XMax − XMin), same for y. `where` = `{X.Name} {x} · {Y.Name} {y}` (tick format).
  - empty block: `{where} — nothing here`
  - else `{where} — {Z.Name} {Z.Format(mean)} from {count:N0} sample` (+`s` unless 1), plus `, spanning {Z.Format(lowest)} to {Z.Format(highest)}` when count > 1 and highest − lowest > 0.
  - No `+` sign on deltas in the scatter readout.

**Open question:** as in the table, the readout (at height − 16) and the X channel name (at height − 2 − textHeight) can overlap. Builder can settle.

#### Click: trace a mark back

Left button down on a hovered block with points (`OnScatterBlockActivated`): samples = the points whose block is this one; visits grouped as for the table (gap ≤ 3 indices); longest = the first visit with the largest (last − first). Then switch LogView to Plot, give the plot all visits as occurrences, zoom to [from − m, to + m] (m = max((to − from)·2, 1.0) s), mark from → to. Status line:
`{X.Name} {x:G6} · {Y.Name} {y:G6} — {Z.Name} {Z.Format(mean)}{spread} from {count:N0} samples over {visits}. Showing the longest ({len:N0} samples).` where `{spread}` = ` ({Z.Format(lowest)} to {Z.Format(highest)})` when count > 1 and spread > 0, else empty; `{visits}` = `one visit` / `{n} visits, all marked`.
The scatter has no selected-block ring.

No right-click, wheel, drag, zoom/pan, keys or tooltips.

#### Status line after a scatter build

- Empty: `Every sample was filtered out — loosen or switch off a filter.` (filters applied, none pass) else `No samples to plot — try a wider time range.`
- Otherwise parts joined by `   •   `: (first, when a compare channel is set) `difference against {Compare.Name}`; `{Count:N0} samples`; when filters applied `{Filtered:N0} of {Count + Dropped + Filtered:N0} excluded by filters`; when any dropped `{Dropped:N0} with a reading missing`; `not in this log: {names}`.

The scatter ignores the second-log comparison and VE Calibration.

---

### VE Calibration (Histogram only)

Purpose: compare the logged mixture (Z, e.g. AFR) against the target the ECU wanted (the `Compare against` channel) per cell and show how far each cell's fuel number should move, or the suggested new numbers. **It only displays and exports; it never changes a tune or an ECU** (see Safety).

Options (visible only while `Suggest a new fuel table` is ticked):

| Control | Rule |
|---|---|
| `VeSummary` text (10 pt `Faint`, wraps) | see below |
| Check `Show the new numbers` (tooltip `Off shows how far each cell moves, as a percentage`) | `VeShowSuggested`, default off → rebuild |
| `Min samples` box (tooltip `A cell with fewer samples is left alone`) | integer, applied on focus loss, min 1, default 12 → rebuild |
| `Max change %` box (tooltip `Largest move suggested for one pass`) | number, on focus loss, clamped 1…100, default 15 → rebuild |
| `Wideband delay, s` label with right button `Find it` (padding 7,2, 10 pt; tooltip `Work the delay out from this log, by finding the one that makes the readings in each cell agree best`) | runs the delay search (below), then rebuild |
| Delay box (tooltip `How long the sensor takes to see the mixture metered now. It depends on where it is fitted — 0.2 to 0.4 s is typical.`) | seconds, on focus loss, clamped 0…2, default 0 → rebuild |
| `VeDelayFinding` (10 pt `Muted`, 3 above) | shown when non-empty |
| `VeDelayNote` (10 pt `Faint`) | always (within the block) |

The checkbox is enabled when a log is open and X and Y are chosen (`VeAvailable`). It does not require a tune. **Open question:** the panel comment says it is offered only once the axis source carries the tune's numbers, but the rule enables it without a tune (binning on the log's own grid). The code rule is the intended one per its doc comment; builder follows the code.

Delay in samples = 0 when delay ≤ 0 or no log; else round(delaySeconds / log median sample interval). `VeDelayNote`:
- delay ≤ 0: `No delay — readings are credited to the moment they arrive.`
- rounds to 0 samples: `Less than one sample at this log's rate, so nothing is shifted.`
- else `{n:N0} sample` (+`s` unless 1) ` at this log's rate.`

#### What the analysis computes

Grid: the axis source's table **with values** when it has one ("from tune"); otherwise a grid built from the log: columns × rows from the Table Size settings, centres = min + (i + 0.5)·step over the window's filtered samples (no data → 0…1; span < 1e-9 → width 1), all values 0, named `{X.Name} × {Y.Name}`.

For each sample i in the window that passes the filters, with j = i + delaySamples ≤ window end and sample j also passing filters: cell from X, Y at i (nearest breakpoint); measured = Z at j; wanted = target at i; skip if any NaN or measured ≤ 0 or wanted ≤ 0. Accumulate ratio measured/wanted per cell.

Sanity check: if mean ratio over all used samples is < 0.5 or > 2 → problem (no table drawn):
`"{Z.Name}" averages {avgZ:G4} and "{target.Name}" averages {avgTarget:G4}, which are not the same kind of number — one is probably lambda and the other AFR. Comparing them would suggest a correction of hundreds of per cent. Pick a measured channel and a target on the same scale.`

Per cell with count ≥ Min samples (cells with 1…min−1 samples are "thin" and left empty):
- ratio = mean(measured/wanted); error % = (ratio − 1)·100.
- confidence = count / (count + Min samples).
- move % = clamp(error·confidence, −Max, +Max).
- If the grid cell's current value > 0: suggested = current·(1 + move/100); change % = (suggested − current)/current·100.
- Else (no tune values): change % = clamp(error, −Max, +Max) (not confidence-weighted); no suggested value.

Shown table:
- Default (change view): cells = change %, 1 decimal, `+` on positives, diverging colours about 0; title `{X}  ×  {Y}   —   {grid name} change, %`.
- `Show the new numbers`: cells = suggested values, 1 decimal, sequential colours; title `… — suggested {grid name}`.
- Counts carry over (including thin cells), so colour-by-count still works.
- No comparison against a second log, no visited rings, no status-line parts for VE tables.

`VeSummary`:
- no target chosen: `Set "Compare against" to the AFR target channel.` (falls back to an ordinary table)
- problem: the problem text (also on the status line; falls back to an ordinary table, whose own status line then replaces it)
- no cell reached the threshold: `Nothing to suggest — no cell reached {Min} samples.`
- else `{suggested} of {cols×rows} cells, {thin} too thin, largest change {largest:F1}%, from {used:N0} samples   ·   {grid}` where grid = the table name when from the tune, else `{grid name} — the log's own bins, not the ECU's`.

Status line on success:
- nothing to suggest: `No cell has enough samples yet. Lower the sample threshold, or drive the untouched areas.`
- from tune: `Suggesting {n} cells of {grid name}. Cells with too little data are left alone. Export the table to paste it into your tuning app.`
- no tune: `Showing how far out {n} cells are, binned on the log's own range rather than the ECU's breakpoints — open the tune, or connect, to get a table that lines up cell for cell with the one you are tuning.`

**Open question:** with `Show the new numbers` on and no tune values, every suggested cell is empty, so the view shows `No samples in this range` while the summary reports N cells. Builder can settle (e.g. disable the check without tune values) or user decides.

**Open question:** thin cells (samples > 0, no value) are drawn as empty cells but are still clickable (count > 0), and their hover readout reads `{Z.Name}  from {n} samples` with an empty value. Builder can settle.

#### Find it (wideband delay search)

Requires a log with samples (`Open a log first.`) and X, Y, Z and a compare target (`Pick the axes and a target channel first.`). Uses the same grid as the analysis but over the **whole log** (not the zoomed window) and the current filters. Sweeps delays 0…min(1.0 s, log length) in at most 40 steps, scoring how well readings agree within each cell; needs ≥ 200 usable samples. Outcomes into `VeDelayFinding`:
- problem (delay box left unchanged): one of
  - `This log is too short, or its samples too far apart, to look for a delay.`
  - `Only {n:N0} samples are usable here, which is too few to tell a delay from noise. Widen the time range, or loosen the filters.`
  - `The readings in this log do not vary enough to find a delay from.`
  - `Changing the delay barely changes the fit, so this log cannot say what it is. That usually means it holds no sharp changes — every delay pairs a cell with readings from the same conditions, so none of them can be told apart. Log a few quick throttle changes and try again.`
  - `The fit was still improving at {best:F2} s, which is as far as this looks. A delay that long is more than a sensor and a pipe, so this is probably something else moving with time rather than a transport delay.`
- success: the delay box is set to the best seconds, and with `{over}` = `over {scored:N0} samples`:
  - precise, 0 samples: `No delay — the readings already line up, {over}.`
  - precise: `{best:F2} s, {over}.`
  - band including 0: `Somewhere between none and {high:F2} s; {best:F2} s fits best, but not by enough to tell them apart {over}. Anything longer is worse.`
  - band: `Between {low:F2} and {high:F2} s; {best:F2} s fits best, {over}.`

**Open question:** the delay box accepts up to 2 s but the search only looks to 1 s. Spec ambiguity; builder keeps both limits.

---

### Comparison against a second log (Histogram only)

File menu items (owned by the menu section; behaviour here):
- `Compare against another log…` (enabled with a log open; tooltip `Open a second log and show what changed between them`): open dialog title `Compare against`, filter `Datalogs|*.mlg;*.msl;*.csv;*.tsv;*.txt|All files|*.*`. Result is Reported:
  - no log: `Open a log first, then a second one to compare it against.`
  - no shared channel names: `That log shares no channel names with this one, so there is nothing to compare. They are probably from different firmware.`
  - load failure: `Could not open that log: {error}`
  - success: `Comparing against {fileName}. {overlap}` with overlap `Both logs carry the same {n} channels.` or `{n} channels in both` + `, {a} only in the first` + `, {b} only in the second` + `.`
- `Stop comparing` (enabled while comparing): Reports `Stopped comparing.`
- `Show the difference` check (enabled while comparing; default on; tooltip `Subtract the second log's table from this one, cell by cell`).

While comparing, showing the difference, and VE Calibration did not produce the table: the second log's table is built from the **whole** second log, on this table's own centres (nearest-centre binning), same statistic, same filters, same compare channel (looked up by name). Then:
- if the second log lacks X, Y or Z by name, or no cell is populated in both, the ordinary table is shown unchanged;
- else each cell = first − second where both have a value (else empty), sample count = the smaller of the two; title `… — {Z.Name} — change`; values in Z's precision; the status line gets `difference against {fileName}` first.

The difference table has no compare channel, so it uses the **sequential** ramp and no `+` sign. **Open question:** a difference is signed and probably wants the diverging scale and `+` sign. User decision.

**Open question:** a `CompareSummary` text is computed (e.g. `{both} cells in both runs · average change {mean:+0.##;-0.##;0} · largest {largest:+0.##;-0.##;0} at {col:N0} × {row:N0}` + ` · {a} cells only this log, {b} only {file}`; or `{file} does not carry all three of these channels, so the difference cannot be worked out. Pick axes both logs have.`; or `The two runs never visited the same cell, so there is nothing to subtract. They may have been driven in different gears or over a different range.`; or `{both} cells overlap but none has enough samples to be worth reporting.`) but is **not shown anywhere**. When a difference cannot be computed the user is told nothing. User decision whether to show it (e.g. under the panel).

---

### Exports (File > Export, enabled when a log is open)

| Item | Visible | Action |
|---|---|---|
| `Table as CSV…` | Histogram | values CSV of the current table |
| `Sample counts as CSV…` | Histogram | counts CSV of the current table |
| `Table as PNG…` | Histogram | image of the HistogramView |
| `Plotted points as CSV…` | Scatter | one row per point |
| `Scatter as PNG…` | Scatter | image of the ScatterView |

Save dialog: suggested name `{logFileStem}-{suffix}{ext}` (stem `log` if the log has no path) with suffix `table` / `counts` / `points` / `table` / `scatter`; filter `CSV file|*.csv|All files|*.*` or `PNG image|*.png|All files|*.*`; adds extension; asks before overwriting; starts in the workspace Exports folder.

Files are written to `{path}.tmp` then moved into place. CSV is UTF-8 **with** a byte-order mark. Failure: message box title `OpenLogViewer`, `Could not save the file.\n\n{error}`, OK, warning icon.

- Table CSV: header row `{Y.Name} \ {X.Name}` then column centres; then rows from the **highest** row down, each starting with its row centre, then cell values (empty for no value). Axis numbers rounded to 4 dp, values to 6 dp, round-trip format, invariant culture. Counts CSV: same layout with integer counts (0 included). Status `Saved the {cols}×{rows} table to {fileName}`.
- Points CSV: two header rows — names `Sample`, `{X.Name}`, `{Y.Name}`, `{Z.Name}` or `{Z.Name} − {Compare.Name}`; units ``, X units, Y units, Z units — then `{sampleIndex},{x},{y},{z}`. Numbers: if the value is exactly representable as 32-bit float, the float's shortest round-trip text, else the double's; NaN empty; invariant. Status `Saved {n:N0} points to {fileName}`.
- Fields containing `,` `"` CR or LF are quoted with `"` doubled.
- PNG: rendered at 2× (192 dpi), size ceil(view size × 2), filled with theme `Background` first. A zero-size view fails with `The view has no size to save.`. Status `Saved the {table|scatter} to {fileName}`.

**Open question:** with no current table (or no points), the CSV items write nothing and say nothing after the user chose a file. Builder can settle (disable, or say why).

**Open question:** the CSV writer's comment says "No BOM" but it writes one. User decision; match C# (BOM) by default.

---

### Scripted-run switches (what modes exist)

| Switch | Effect |
|---|---|
| `--cell c,r` | rebuild, then trace table cell (c, r) (processed before `--histogram`) |
| `--histogram` | switch to Histogram. Then optional: `--z <name>` picks Z from **all** channels by name; `--tune-axes N` selects axis source N when 0 < N < count; `--compare <name>` sets the Compare channel by name; `--count-colour` colour by count; `--count-value` Count statistic; `--ve` turns VE Calibration on, `--ve-values` also ticks `Show the new numbers`. |
| `--scatter` | switch to Scatter; `--z`, `--compare`, `--count-colour` as above; `--mark c,r` rebuilds, lays out, and traces block (c, r). |
| `--ecu-ve` | (live session, after the settle delay) select the first axis source whose label contains `VE Table` and `from the ECU`; Reports `using the ECU's fuel table` or `no ECU fuel table offered`; with `--ve` / `--ve-values` as above. |
| export-all (scripted) | in Histogram writes table CSV, counts CSV, table PNG; in Scatter points CSV and scatter PNG; into a folder, no dialogs. |

Note `--compare` here means the Z compare channel, not a second log.

---

### Open questions

1. Table rebuilt while the Plot is showing (anything not Scatter). Builder can settle.
2. Hover readout overlaps the X-axis name in both views. Builder can settle.
3. VE/difference hover readout labels the value with `{Z.Name}` though it is % or VE. Spec ambiguity, builder can settle.
4. Selected (traced) cell ring never cleared; survives table changes. Builder can settle (likely bug).
5. Trace-back with `Only the zoomed time range` on rebuilds the table from the trace's zoom window. User decision.
6. Diverging legend uses Z precision, not the table's display digits. Builder can settle.
7. VE checkbox enabled without a tune, against the panel comment. Builder follows the code.
8. `Show the new numbers` without tune values shows `No samples in this range`. Builder can settle / user decision.
9. Thin VE cells are clickable and show an empty value in the readout. Builder can settle.
10. Delay box allows 2 s; search covers only 1 s. Builder keeps both.
11. Difference table uses the sequential scale and no `+` sign. User decision.
12. `CompareSummary` is computed but never shown; a failed difference is silent. User decision.
13. CSV exports with nothing to export do nothing silently. Builder can settle.
14. CSV says "No BOM" in a comment but writes a BOM. User decision.
15. (minor) `LoadComparison`'s no-shared-channel branch nulls the overlap before reading its summary, so the fixed fallback text is always used. Builder: use the fallback text.

### Safety rules found

- **VE Calibration has no path into a tune or ECU.** It only draws a table and can be exported as CSV (`Table as CSV…`). Nothing in these modes writes, sends or burns. The success status line tells the user to `Export the table to paste it into your tuning app.` The Rust port must not add an apply-to-tune path without a separate, explicit design.
- **Opened tune differs from the log's tune** (trigger: Open tune… while the log embeds a tune and `VE table 1` values differ): the panel and status line show, in `Marker` colour, `This tune's fuel table differs from the one stored in the log. VE Calibration scales the numbers that produced the logged AFR, so suggestions will be off by however much the tune has changed since the drive.` and the toolbar tune indicator turns `Marker`.
- **AFR/lambda scale mismatch guard** (trigger: mean measured/target ratio < 0.5 or > 2): no VE table is drawn; the `"{Z}" averages … not the same kind of number …` message is shown instead.
- **Delay finder never invents a value**: on any problem result the delay setting is left unchanged.
- ECU tables offered as axis sources (`… — from the ECU`) are read-only inputs here; selecting one only rebins.


## §4 `w7-calibration`: tune tables, curves, settings pages, saved tunes

### Scope and terms

The Calibration mode shows the tune: the tables, the settings pages, and the curves. It covers the
Calibration workspace, the tune table view, the curve view, the settings page form, the four
saved-tune commands in the Tools menu, and every send, burn and restore that starts from them.

Terms used below:

- **tune in hand**: the tune the view model holds (`_ecuTune`). It comes from one of four sources:
  1. **read off the ECU** at connect (TunerStudio page protocol: `ReadTuneFromEcu`; MaxxECU USB: `AdoptMaxxTune`),
  2. **opened from a saved .msq** (`OpenSavedTune`), which sets `TuneIsFromFile = true`,
  3. **opened from a firmware definition (.ini)** with no tune (`OpenDefinition`, used only by scripted
     runs), which sets `TuneIsPlaceholder = true`. Every value is zero.
  4. none.
- **connected**: a TunerStudio-protocol `EcuConnection` is held (`_ecuConnection != null`). A MaxxECU
  USB session holds `_maxxUsbSource` and does **not** set `_ecuConnection` (see Open questions).
- **table edit**: the working copy of the selected table (`TableEdit`, a `TuneEdit`). It holds the
  values as read (the *original*) and the edited values.
- **settings edit**: the working copy of every settings page (`_settingsEdit`, a `TuneSettingsEdit`).
- **pages written**: the set of page indices sent since the tune was read (`_settingsPagesWritten`).
  A settings burn commits these pages.
- **hint**: the application's one-line status/hint text, set with `MainViewModel.SetHint`/`Hint`.
  Its home belongs to another section. "Report(x)" below means: set the hint to x and also append
  `HH:mm:ss  x` to the run log file `%TEMP%\openlogviewer-run.log`. If the append fails, the error is ignored.

All number formatting uses the current culture unless this section says otherwise.

---

### Layout of the Calibration workspace

The workspace is shown when `InCalibrationMode` is true. It is a three-column grid:

| Column | Width | Content |
|---|---|---|
| 0 | 260 px (min 180), user-resizable | Left list panel |
| 1 | 4 px | Vertical splitter, colour `Line`, drag to resize column 0 |
| 2 | remaining (min 300) | Right work area |

Visibility rules for the whole workspace:

| Element | Shown when |
|---|---|
| Left panel, splitter, right work area | `HasEcuTune` (the tune in hand is not null) |
| No-tune notice (centred in column 2, `Muted`, centred text, two lines): `Connect to an ECU to read its tune.` / `Tables come off the controller itself, not from a saved file.` | `ShowNoTuneNotice` = no tune in hand AND NOT `IsObd2Live` |
| Fault-codes panel (another section's control), spanning all three columns | `IsObd2Live` (a live OBD2 session). The panel is attached at window start-up and scans on first sight. |

Because the column sizes stay fixed when their content is hidden, the no-tune notice is centred in
column 2 (to the right of an empty 264 px gutter). The fault panel spans all three columns for this
reason.

#### Left list panel

The panel uses background `Panel` and has a 1 px right border in `Line`. It has a 10 px inner margin.
From top to bottom:

1. **Half chooser**: two chip-style radio buttons in one group, side by side, with a bottom margin of 8.
   | Chip | Selected when | Enabled | Tooltip |
   |---|---|---|---|
   | `Tables` | `ShowEcuTables` (= NOT `ShowSettingsPages`) | always | `The maps: VE, ignition, AFR target` |
   | `Settings` | `ShowSettingsPages` | `HasSettingsPages` (the settings menu has at least one entry) | `Everything else the firmware lets you change` |
   Clicking a chip switches halves. `ShowSettingsPages` defaults to false, so the panel opens on Tables.
2. **Heading** `TABLES IN THE ECU`. Size 10, SemiBold, `Muted`, bottom margin 6. Tables half only.
3. **Table search box**. Bottom margin 6. Tables half only. Tooltip `Filter by name — try VE, ignition, boost`.
   Typing filters the table list. The filter runs 120 ms after the last keystroke. It keeps tables whose
   name contains the text, ignoring case. An empty box shows all tables.
4. **Settings pages list**. Settings half only. No border, transparent background, no horizontal scroll,
   and text trimmed with an ellipsis. One line per `SettingsMenuEntry`:
   - **Heading** (`IsHeading`, the menu's name): size 10, SemiBold, `Muted`, margin 2,9,2,2. It cannot
     be selected or focused.
   - **Page entry**: `Text` ink, margin 2,3, showing `Title`. Selecting it sets `OpenMenuEntry` and
     opens the page (see "Opening a settings menu entry").
5. **List summary** (docked at the bottom, top margin 6, size 11, `Muted`). Shows `EcuTableSummary`:
   empty when there are no tables, `{N} tables` when nothing is filtered out, and `{shown} of {N} tables`
   when the filter hides some. It is shown in **both** halves (see Open questions).
6. **Table list** (fills the rest). Tables half only. Same styling as the pages list. Each line shows the
   table's label `{Name}  ({Columns}×{Rows})`, with two spaces before the bracket and `×` as U+00D7.
   Selecting a line sets `SelectedEcuTable`.

**Table order**: largest cell count (columns × rows) first. Among tables of equal size, those whose name
contains `VE` (ignoring case) come first. Ties then sort by name, ignoring case.

**Settings menu build** (`BuildSettingsMenu`): the list is rebuilt from the firmware's menus every time
a tune is adopted. For each menu:

- Skip separators and the tool's built-in entries.
- Keep an entry only if its target is a dialog, a buildable curve, or a known table. The checks run in
  that order. A table matches when its table-editor id or map name equals the target, ignoring case.
  For curves, see the curve rules below.
- The entry's title is the menu entry's title, or its target name when the title is empty.
- A menu with no kept entries is dropped. Otherwise add a heading with the menu's title, followed by
  its entries.
- Each entry's condition is stored but **never evaluated**: every page is listed (see Open questions).

#### Right work area, top to bottom

The right work area stacks the parts below. "Tables half" means `ShowEcuTables`; "settings half" means
`ShowSettingsPages`.

1. **Tune summary**, always shown. Margin 12,10,12,8. Size 11, `Muted`, wraps. Text = `EcuTuneSummary`
   (formats under "Tune summary texts").
2. **Tool strip**, tables half only. Panel border, see "Table tools".
3. **Change preview strip**, tables half only. See "What it would become".
4. **Curves area**, shown when `ShowCurves` = settings half AND at least one curve is open.
   Minimum height 340.
5. **Tune table view**, shown when `ShowTableView` = tables half OR (settings half AND the open menu
   entry is a table). In the tables half it fills the space left between the strips above and the key
   hint below.
6. **Settings page form**, shown when `ShowSettingsFields` = settings half AND a page is open AND the page
   has at least one visible row. It fills the rest of the area.
7. **Key hint line** (bottom, above the action bar), tables half only. Margin 12,0,12,0. Size 10, `Muted`.
   Exact text:
   `Click or drag to select · arrows move · shift extends · + − nudge · PgUp/PgDn scale by 1% (shift 5%) · Esc undoes the selection`
8. **Action bar** (bottom). Margin 12,8,12,10. Its buttons are laid out left to right:

| Button | Half | Right margin | Enabled when | Action | Tooltip (exact) |
|---|---|---|---|---|---|
| `Send to ECU` | tables | 8 | `CanWriteTable` | Send-table flow | `Writes the changed cells into the ECU's working memory. Takes effect immediately; lost at the next power cycle unless burned.` |
| `Burn` | tables | 8 | `CanBurn` | Burn-table flow | `Commits the page to the ECU's flash. This survives a power cycle.` |
| `Revert` | tables | 14 | `HasTableChanges` | `RevertTable()`: every cell back to the original, no confirmation | `Puts every cell back to what the ECU said.` |
| `Send to ECU` | settings | 8 | `CanWriteSettings` | Send-settings flow | `Writes the changed settings into the ECU's working memory. Takes effect immediately; lost at the next power cycle unless burned.` |
| `Burn` | settings | 8 | `CanBurnSettings` | Burn-settings flow | `Commits the pages that were written to the ECU's flash. This survives a power cycle.` |
| `Revert settings` | settings | 14 | `HasSettingChanges` | `RevertSettings()`: every changed setting back, no confirmation | `Puts every changed setting back to what the ECU said.` |

After the buttons comes a status text: size 11, `Muted`, vertically centred, trimmed with an ellipsis.
It shows `TableEditSummary` in the tables half and `SettingsSummary` in the settings half.

**`TableEditSummary`**: empty when no table is open. Otherwise it is `{where}{value}   ·   {changed}`,
with three spaces on each side of the dot:

- `where`:
  - one cell selected: `{xBreakpoint:G5} × {yBreakpoint:G5}`. The breakpoints are the selected cell's
    column and row breakpoints.
  - several cells: `{columns}×{rows} cells`.
- `value`: for one cell, `  =  {cell value} {units}`, with two spaces on each side of `=`. The cell
  value uses the format `"0." + "#" × max(1, Digits)`. For several cells it is empty.
- `changed`:
  - 0 changed cells: `no changes`
  - 1: `1 cell changed, not sent`
  - more: `{n} cells changed, not sent`

The count covers the whole table.

**`SettingsSummary`**: empty when there is no settings edit. With 0 changes:
`{pages:N0} pages · nothing changed`, where pages = the number of non-heading menu entries. Otherwise:
`{n:N0} setting{s} changed · {bytes:N0} bytes to send`. The `s` is dropped when n is 1.

---

### Tune table view (custom-drawn)

A focusable control that clips to its bounds. It draws `TableEdit.AsTable()` (the edited values) when a
table edit exists, otherwise `SelectedEcuTable`. Each table selection creates a **fresh table edit**
(see State rules). Every theme change redraws the view.

#### Geometry

| Constant | Value |
|---|---|
| Label column (row breakpoints) width | 58 |
| Label row (column breakpoints) height | 22 |
| Padding on every side | 10 |

- Grid area: x from 10+58 = 68, y from 10. Width = ActualWidth − 58 − 20; height = ActualHeight − 22 − 20.
- If width < 40 or height < 40, or the table has 0 columns or 0 rows, only the background is drawn.
- cellWidth = width / columns; cellHeight = height / rows.
- **Load increases upward**: row 0 (the first Y breakpoint) is the bottom row. The top of row r is
  y = 10 + (rows − 1 − r) × cellHeight. The left of column c is x = 68 + c × cellWidth.
- Hit-testing uses the geometry of the last draw. column = floor((px − 68) / cellWidth) and
  row = rows − 1 − floor((py − 10) / cellHeight). A point outside the grid hits no cell.

#### Draw order

1. Fill the whole control with `Background`.
2. For each row, and in it each column:
   - **Heat style** (`theme.Style.Table == Heat`, the default): fill the cell rectangle
     (x, y, cellWidth + 0.5, cellHeight + 0.5) with the heat colour. The extra 0.5 hides the seams.
   - **Grid style** (`Table == Grid`, used by the Retro DOS theme): no fill, so the background shows.
     Stroke the rectangle (x, y, cellWidth, cellHeight) with a 1 px pen in the theme token `Grid`.
   - **Changed cell** (see "Changed-cell marking"): stroke the cell rectangle (x, y, cellWidth + 0.5,
     cellHeight + 0.5), inset 1.5 px on every side, with a 2.5 px pen in `Marker`. This is the same in
     both styles.
   - **Value text**, only when cellWidth ≥ 26 AND cellHeight ≥ 12. It is centred in the cell.
     - Font: the theme's DataFont (`theme.Style.DataFont`). Bold when the cell is changed, normal
       otherwise.
     - Size: min(11, cellHeight × 0.62).
     - Format: `"0." + "#" × max(1, Digits)` when a table edit exists, otherwise `"0.#"`.
       `Digits` = the firmware constant's declared digits, or 0 when none is known.
       So 0 digits still shows up to one decimal.
     - Ink, in this order:
       - changed → `Marker`;
       - otherwise Grid style → `Readout`;
       - otherwise Heat style → `#0B0B0B` when the fill's WCAG relative luminance > 0.5, else `#FFFFFF`.
   - After the row's cells, when cellHeight ≥ 11: the **row label** (that row's Y breakpoint, `G5`
     format). DataFont, size 10, `Muted`. Right-aligned so it ends 5 px left of the grid
     (x = 68 − 5 − textWidth), centred vertically in the row.
3. **Column labels** (X breakpoints, `G5`, DataFont, 10, `Muted`). They are thinned: only every
   step-th column is labelled, step = max(1, ceil(34 / cellWidth)), starting at column 0. Each is
   centred under its column, with the top at y = 10 + height + 4. They are always drawn; there is no
   minimum size.
4. **Grid outline**: a 1 px pen in `Line` around the grid area (68, 10, width, height).
5. **Selection**, drawn only when a table edit exists. A rectangle around the clamped selection, from
   the left column to the right column and from the bottom row to the top row:
   left = 68 + Left × cellWidth, top = 10 + (rows − 1 − Bottom) × cellHeight, size
   Columns × cellWidth by Rows × cellHeight.
   - Pen: 2 px. `Accent` in Heat style, `Readout` in Grid style.
   - It is not inset.
6. **Units**: the table's units string, when not empty. DataFont, 10, `Text`, at (10, 10 + height + 4).
   That is the bottom-left corner where the two axes meet.

#### Heat colour (Heat style only)

- ramp = `theme.SequentialRamp`: an ordered array of colours, owned by the theme section.
- Extent: low and high are the min and max of all non-NaN cell values **of the table as drawn**.
  This is the edited values, so the scale shifts as cells are edited. With no non-NaN values,
  the extent is (0, 1).
- The colour for a value v:
  - empty ramp OR v is NaN → `#808080` (grey);
  - high ≤ low → ramp[len / 2], using integer division;
  - otherwise:
    - at = clamp((v − low) / (high − low), 0, 1) × (len − 1);
    - i = floor(at);
    - if i ≥ len − 1, the colour is ramp[len − 1];
    - else it is Blend(ramp[i], ramp[i + 1], at − i).
- Blend mixes each RGB channel linearly, a + (b − a) × t, rounded half-to-even (.NET `Math.Round`),
  with t clamped to 0..1.
- Luminance = 0.2126·R + 0.7152·G + 0.0722·B, where each channel is linearised by the sRGB
  transfer curve (WCAG).

#### Theme style summary (the two looks)

| Aspect | Heat (default themes) | Grid (`TableStyle.Grid`, Retro DOS) |
|---|---|---|
| Cell fill | heat colour | none (`Background` shows) |
| Cell border | none | 1 px `Grid` per cell |
| Unchanged value ink | #0B0B0B or #FFFFFF by fill luminance | `Readout` |
| Changed value ink / outline | `Marker`, bold / 2.5 px `Marker` | same |
| Selection pen | 2 px `Accent` | 2 px `Readout` |
| Fonts | theme DataFont (Consolas by default) | the Retro theme's DataFont (the bundled retro pixel font) |

The theme style has two more global settings. `CornerScale` (0 for Retro) scales the corner radius of
the tool and preview strips through `CornerMedium`. `PixelText` turns on whole-pixel text rendering.
Both belong to the theme section, and the table view itself does not read them.

#### Changed-cell marking

A cell is changed when |edited − original| > Step / 1000, where:

- original = the value when this table edit was created;
- Step = |constant.Scale| when the constant's scale is > 0, otherwise 1.

The comparison is against the copy read when the table was selected or last re-read after a send. It
is not against a saved file. After a successful send the table is re-read, a fresh edit is made, and
the marks clear.

#### Not drawn

The WPF view has none of these:

- an operating-point marker;
- a live trail;
- a hover readout or tooltip;
- a ghost of the original values.

(See Open questions.)

#### Mouse

| Input | Effect |
|---|---|
| Left press on a cell | Focus the view. Without Shift: select that single cell. With Shift: keep the anchor corner (From) and move the other corner (To) to this cell. Then capture the mouse and publish the selection (`SelectedCells`). |
| Left press outside the grid | Focus the view only. The selection does not change and there is no capture. |
| Move with the left button held while captured | If the pointer is over a cell, move the To corner to it. Off the grid, nothing changes. |
| Left release | Release the capture. |
| Right-click, double-click, wheel | Nothing. There is no context menu. |

#### Keyboard

The view must have keyboard focus, and the keys do nothing when there is no table edit. Shift is
"extend". All of these change only the on-screen copy; none sends anything.

| Key | Action |
|---|---|
| Left / Right | Move the To corner by −1 / +1 column, clamped to the table. Without Shift, the selection collapses to that single cell. With Shift, the From corner is kept (the selection grows or shrinks). |
| Up / Down | The same for rows. **Up = row + 1 (higher load, drawn higher on screen)**; Down = row − 1. |
| `=`/`+` key (OemPlus) or numpad `+` | Add +Step to every selected cell, or +Step × 10 with Shift. |
| `-` key (OemMinus) or numpad `−` | Add −Step to every selected cell, or −Step × 10 with Shift. |
| PageUp | Scale the selected cells by +1 %, or +5 % with Shift. |
| PageDown | Scale by −1 %, or −5 % with Shift. |
| Esc | Revert the selected cells to their original values. |
| anything else | Ignored and left unhandled. Tab moves focus as usual. |

The nudge keys use **Step**, the firmware storage step, **not** the value in the tool strip's "by" box.
On a main keyboard, the `+` character needs Shift, so it gives a ×10 nudge (see Open questions).

Ctrl with `+`, `−` or `0` is caught at window level before the table sees it, but only when Ctrl is
the **only** modifier. It zooms the interface (in, out, reset) and the table does not act.

These table keys do **not** exist:

- typing a number into a cell;
- Enter;
- Delete;
- Ctrl+C / Ctrl+V;
- Ctrl+Z / undo.

Setting a value is done with the tool strip's `Set to` box.

#### Edit rules (applied by the view model, `EditTable`)

Every edit applies to the selection, after clamping the selection to the table. After each edit, the
summary, the preview, `HasTableChanges` and `CanWriteTable` are refreshed.

| Kind | New value per cell |
|---|---|
| Add(a) | current + a |
| Scale(p) | current × (1 + p / 100) |
| Set(v) | v |
| Revert | original. No clamp. |
| Interpolate | Needs at least 3 columns OR at least 3 rows; otherwise the hint is set to `Select at least three cells across or down — interpolation fills what is between the ends.` and nothing changes. Otherwise every cell becomes the bilinear blend of the four corner values of the selection (see below). The corners keep their values. |

Interpolate detail: across = (c − Left) / (Columns − 1), or 0 when there is one column. down is the
same for rows. top = TL + (TR − TL) × across, bottom = BL + (BR − BL) × across, and the cell becomes
top + (bottom − top) × down. Here "Top"/TL means the lowest row index, which is drawn at the bottom.

**Hold (clamping)**:

- Every Add, Scale, Set and Interpolate result goes through Hold.
- NaN or ±∞ becomes 0.
- When the firmware constant declares a range, the value is clamped to [Low, High]. Out-of-range edits
  are clamped, not refused.
- Values are **not** snapped to the storage step in the edit. Snapping happens only when the table is
  encoded.
- When no constant is known for the table, there is no clamping, Step = 1, Digits = 0, and the table
  cannot be encoded or sent. Sending answers `This table cannot be encoded for this firmware, so nothing
  was sent.`

The constant for a table is the table-editor definition whose title equals the table name. Its
`Values` constant is then looked up in the layout.

#### Read-only rules

- The view has no read-only mode. Tables can be edited in **every** state that has a tune in hand:
  placeholder, from file, disconnected, or connected.
- Only sending and burning are gated (see State rules).
- No per-table read-only flag is read.

---

### Table tools strip

The tool strip is shown in the tables half only. It has margin 12,0,12,8, padding 10,7, corner radius
`CornerMedium`, background `Panel` and a 1 px border in `Line`. Its controls wrap onto further lines
when they do not fit.

In order, left to right:

| Control | Detail |
|---|---|
| Label `Add / subtract` | size 11, `Muted`, right margin 6 |
| Button `− {TableNudge:0.###}` | Text is `TableNudgeDownLabel`, using the U+2212 minus sign. Min width 52. Action: Add(−TableNudge). Tooltip `Takes that much off every selected cell, in the table's own units` |
| Button `+ {TableNudge:0.###}` | Min width 52. Action: Add(+TableNudge). Tooltip `Adds that much to every selected cell, in the table's own units` |
| Label `by` | |
| Number box, 52 wide, right-aligned | Two-way with `TableNudge`, updated on every keystroke. Tooltip `How much one press is worth, in the table's own units. Defaults to the firmware's storage resolution — anything finer is rounded away by the ECU.` |
| Units label | `TableStepUnits` = the table's units. Right margin 18. |
| Label `Scale by` | |
| Button `− {TableScaleStep:0.###}%` | Action: Scale(−TableScaleStep). Tooltip `Makes every selected cell that much smaller, as a proportion of what it already is` |
| Button `+ {TableScaleStep:0.###}%` | Action: Scale(+TableScaleStep). Tooltip `Makes every selected cell that much larger, as a proportion of what it already is` |
| Label `by` | |
| Number box, 44 wide, right-aligned | Two-way with `TableScaleStep` (default 1). Has no tooltip. |
| Label `% of the value` | Right margin 18. |
| Label `Set to` | |
| Text box `SetValueBox`, 60 wide, right-aligned | Tooltip `Type a value and press Enter, or use Apply`. Enter applies the value. |
| Button `Apply` | Applies the `Set to` value. Right margin 16. |
| Button `Interpolate` | Action: Interpolate. Tooltip `Fills the inside of the selection from its ends — select three or more cells across or down` |
| Button `Revert cells` | Enabled when `HasTableChanges`. Action: revert the selected cells. Tooltip `Puts just the selected cells back to what the ECU said` |

How the strip behaves:

- All three boxes select their whole contents when they gain focus. A first click on an unfocused box
  focuses it and selects everything, instead of placing the caret.
- **Set to / Apply**: the text is parsed as a float in the current culture. If it does not parse,
  including when it is empty, the hint is set to `Type a number to set the selected cells to.` and
  nothing changes. Otherwise Set(value) runs.
- `TableNudge` resets to the table edit's Step every time a table is selected. Its default before any
  table is 1.
- Text in the nudge or scale box that is not a number is not accepted: the stored value keeps its last
  value. Zero and negative values are accepted.
- The tool buttons are not disabled when there is no table. They do nothing when no table edit exists.

---

### "What it would become" preview strip

The preview strip is shown in the tables half only. Its frame matches the tool strip.

The strip reads `TableEdit.Preview(SelectedCells)`, which works over the clamped selection:

- Cells = the number of selected cells.
- Changed = the number of changed cells in the selection.
- From = the min and max of the original values.
- To = the min and max of the edited values.
- Delta = the min and max of (edited − original).

Its parts, left to right:

| Part | Style | Text |
|---|---|---|
| Where | 11, `Muted`, right margin 14 | One cell: `{xBp:G5} {xUnits}` + ` × ` + `{yBp:G5} {yUnits}`. Each half has its trailing spaces trimmed. The units are the axis's plain units (empty when the unit is an expression). Several cells: `{columns}×{rows} cells`. Empty with no table. |
| From | DataFont, 15, `Muted` | One cell: Format(From). Several: `{Format(FromLow)}–{Format(FromHigh)}` with an en dash. |
| Arrow `→` | 14, `Muted`, margin 8,0 | Shown only when Changed > 0 |
| To | DataFont, 15, SemiBold, `Accent` | Like From, but for the To values. Shown only when Changed > 0. |
| Delta | 11, `Muted`, left margin 14 | Empty when Changed = 0. See the formats below. |
| State (right-aligned) | 11, `Muted` | Changed > 0: `not sent · {ChangedCount of whole table} changed in this table`. Otherwise `unchanged`. Empty with no table. |

Format(v) = `"0." + "#" × max(1, Digits)`, or `0.##` when there is no table edit.

Delta formats use the custom format `+0.###;−0.###`: the second section has a literal U+2212 minus
sign, and zero takes the positive form `+0`.

- One cell, original ≠ 0: `{Delta} {units}` trimmed, then `   ({|pct|:0.0}% more)` or `% less)`.
  The leading three spaces are part of the text. pct = (To − From) / From × 100, and "more" means
  pct ≥ 0.
- One cell, original = 0 (|From| ≤ 1e-9): `{Delta} {units}` trimmed, with no percent.
- Several cells, all deltas equal (within 1e-9): `{DeltaLow} {units} on every cell`, trimmed.
- Several cells, not uniform: `{DeltaLow} to {DeltaHigh} {units}`, trimmed.

---

### Curves

#### Which curves exist

A curve from the firmware is offered only if a curve edit can actually be built from it against the
tune in hand: both rows exist and have the same length. The names of those curves form `_curveNames`.

#### Opening a settings menu entry (`OpenPage`)

When the user picks a settings menu entry:

1. **Dialog**: if the entry is not a heading, not a curve, and there is a tune and an interface, build a
   `SettingsDialog`. Otherwise the open dialog becomes null.
2. **Curves**: for an entry that is a curve, the curve list is that one curve. For an entry that is a
   dialog, it is the curves named by panels on the page that are currently shown (see "Settings page").
   Each curve is built from the tune in hand, and curves that cannot be built are skipped.
3. **Table**: if the entry is a table, `SelectedEcuTable` becomes that table, which makes a fresh table
   edit. The table view then appears in the settings half (see Open questions).
4. **Rows**: the page's conditions are evaluated. Each row's change event is wired so that any change
   re-evaluates the whole page (rows and curves). Each row's refusal is wired to set the hint to the
   reason.

#### Curves area

The curves area is shown in the settings half when at least one curve is open. Top to bottom:

- **Hint line**: margin 18,0,18,6, size 10, `Muted`, wraps. Exact text:
  `Drag a point to move it · shift-drag moves the breakpoint · arrows nudge by one step, ctrl for ten`
- **Plots**, in a scroll area (vertical only). One block per curve:
  - minimum height 300;
  - title = the curve's title, or its name when the title is empty. Size 16, SemiBold, `Text`,
    wraps, margin 18,6,18,2;
  - below the title, the curve view with margin 12,0,12,0.
- **Bottom bar**: margin 18,6,18,10. Its parts:

| Button | Padding | Enabled when | Action | Tooltip |
|---|---|---|---|---|
| `Send curve to ECU` | 12,5 | `CanWriteCurve` | Send-curve flow | `Writes the moved points to the controller's working memory` |
| `Burn` | 12,5 | `CanBurnSettings` | **The same burn-settings flow** as the settings half | `Commits what has been sent to flash, so it survives a power cycle` |
| `Revert curve` | 12,5 | `HasCurveChanges` | Revert every point of every open curve. No confirmation. | `Puts every point back to what the ECU holds` |
| Summary text | — | — | `CurveSummary`, size 11, `Muted` | — |

**`CurveSummary`**: empty when no curve is open.

- "what" is `{Title} — {Count} points` for one curve, or `{n} curves` for several.
- moved = the number of changed points across all open curves.
- moved > 0: `{what} · {moved} point{s} moved, not yet sent`. The `s` is dropped when moved is 1.
- Otherwise: `{what} · nothing changed`.

A point is changed when |x − origX| > 1e-9 OR |y − origY| > 1e-9.

#### Curve view (custom-drawn)

The curve view is focusable. It shows one curve edit. Changing which curve it shows clears the hover,
ends any drag, releases the mouse capture and frees the held axes.

**Geometry**: the plot rectangle runs from left 62 and top 16, with 18 px to spare on the right and
34 px at the bottom. Width = max(1, W − 62 − 18); height = max(1, H − 16 − 34). Nothing but the
background is drawn when the plot is under 20 × 20 or the curve has no points.

**Axis span**, worked out separately for X (breakpoints) and Y (values):

1. While a drag is in progress, use the span held at the start of the drag.
2. Otherwise, if the axis constant declares a range with High > Low:
   - used = the max of the points minus their min; declared = High − Low.
   - If used > 0 AND declared > 4 × used: the span is
     (max(Low, min − 0.15 × used), min(High, max + 0.15 × used)).
   - Otherwise the span is (Low, High).
3. Otherwise, use the min and max of the points:
   - if they are equal (within 1e-9), the span is (v − 1, v + 1);
   - otherwise widen each end by 10 % of the range;
   - with no points, the span is (0, 1).

**Mapping**: a value maps linearly into the plot, clamped to the plot's edges. A zero-width span maps
to the middle. Y increases upward.

**Draw order**:

1. Fill the whole view with `Background`.
2. Grid:
   - stroke the plot rectangle with a 1 px `Grid` pen;
   - draw 3 interior horizontal and 3 interior vertical lines at quarters, in the same pen;
   - Y labels at the top, the three quarters and the bottom (yHigh to yLow), right-aligned to end at
     x = 56, centred vertically on their line;
   - X labels at the left, the three quarters and the right, centred at y = plot.Bottom + 14.
   - Label format: `F{clamp(digits, 0, 3)}`, using the digits of that axis's constant (0 when none).
   - Labels: UiFont, size 11, `Muted`.
3. Axis titles, in UiFont 11, `Text`:
   - X title: `{XLabel} ({XUnits})`, or just `{XLabel}` when there are no units. Centred at
     (plot centre, plot.Bottom + 28).
   - Y title: `{YLabel} ({YUnits})`, or just `{YLabel}`. Left-aligned at x = 2, centred on
     y = plot.Top − 10.
   - The labels fall back to the names of the bins rows when the curve gives none.
4. **The original line**, drawn only when the curve has changes: a polyline through the original
   points. 1 px `Muted`, dashed 4-on 4-off.
5. **The current line**: a polyline, 2 px `Accent`.
6. **Points**: a circle of radius 4.5 at each point.
   - Fill: `Accent` when the point has moved, `Background` otherwise.
   - Outline: 2 px `Text` when the point is hovered or being dragged, 1.6 px `Accent` otherwise.
7. **Hover readout**, when a point is hovered. Drawn at (plot.Left + 8, plot.Top + 4), in `Text`,
   UiFont 11: `{x} {XUnits}  →  {y} {YUnits}`, with two spaces on each side of the arrow. A moved
   point adds `   (was {origY})`.

**Mouse**:

| Input | Effect |
|---|---|
| Move, not dragging | The hovered point is the nearest point within 9 px (squared distance ≤ 81), or none. The cursor is the north-south resize cursor over a point and the arrow elsewhere. Redraw when the hovered point changes. |
| Left press | Find the nearest point within 9 px. If there is none, nothing happens: no focus, no drag. Otherwise begin a drag of that point. Holding Shift at the press drags the **breakpoint (X)**; without Shift it drags the **value (Y)**. Hold both axis spans, take focus, capture the mouse. |
| Move while dragging | If the capture has been lost or the left button is up, end the drag at once and change nothing. Otherwise: for Y, set the value at the pointer's height (clamped to the plot, then to the constant's range). For X, set the breakpoint at the pointer's x (clamped to the plot, then to the constant's range, then between the neighbouring breakpoints; when the neighbours are inverted, do nothing). Redraw and raise *edited*, which refreshes the summary and buttons. |
| Left release while dragging | End the drag, free the held spans, release the capture, redraw, raise *edited*. |
| Mouse leaves | If not dragging, clear the hover. |

**Keyboard**: works only while the view has focus AND a point is hovered. Focus comes only from
pressing on a point, and leaving the view clears the hover.

- step = |Y constant scale| when the scale is > 0, otherwise 1. Ctrl multiplies it by 10.

| Key | Effect |
|---|---|
| Up / Down | The hovered point's value ± step, clamped to the range |
| Left / Right | Move the hover to the previous or next point, stopping at the ends |

The key is handled, the view redraws and *edited* is raised.

No other keys, no context menu, no wheel.

---

### Settings page form

The form is shown when `ShowSettingsFields` is true. It sits in a vertical scroll area. Its column is
max 640 px wide and left-aligned, with margin 18,4,18,20.

1. **Title**: the open dialog's title, size 16, SemiBold, `Text`, wraps, bottom margin 10. The title is
   the dialog's own title, or the menu entry's title when the dialog's own is empty.
2. **Partial-page notice** (size 10, `Marker`, wraps, bottom margin 10). Shown when the page contains any
   item that cannot be drawn (a live graph or an indicator lamp). Exact text:
   `Part of this page is a live graph or a lamp, which is not drawn here.`
3. **Rows**: one per visible row, each with bottom margin 5. Each row is a three-column line:
   - column 1, 250 px: the label. Size 12, `Text`, wraps, centred vertically, right margin 10.
   - column 2, the rest of the width: the control.
   - column 3, sized to fit: the units and the uncertain mark, with left margin 7.

   The dialog's help text is not shown.

#### Building the page (`SettingsDialog.Build`)

- Find the named dialog in the firmware interface, or show none.
- Walk its items in order:
  - **Panel item** naming another dialog: insert that dialog's rows in place, recursively. A dialog
    already on the current path is skipped, so cycles stop. If the panel has a condition, every row it
    contributes gets the condition `({rowCondition}) && ({panelCondition})`, or just the panel
    condition when the row has none.
  - **Panel item** that names no dialog but names a known buildable curve (and the curve is not
    already listed, comparing names without case): record the curve together with the panel's
    condition. Curves are shown until the first evaluation.
  - **Panel item** naming neither: skip it.
  - **Any other item**: becomes a row, joined to the constant it names (or none).

#### Row kinds and controls

| Kind | Rule | Column 2 control | Editable |
|---|---|---|---|
| Caption | Item is a label or a text line, OR it names a constant this firmware does not declare | nothing. The label alone, which may be blank. | no |
| ReadOnly | Item is a read-only field | text of the current value, size 12, `Muted` | no |
| NotShown | Item is unsupported or a gauge | the row is never listed. It makes the page "partial". | — |
| Text | Constant is a string | text box, size 12 | if the constant is on the controller |
| Choice | Constant has options (bits or a list) | drop-down list, size 12 | if on the controller |
| Number | anything else | text box, size 12 | if on the controller |

- **Value shown**:
  - Text: the edited string.
  - Choice: the name of the option at the edited value.
  - Number: the edited value with `F{clamp(Digits, 0, 6)}`. NaN shows as empty.
  - Each element of an array constant is addressed by the item's target index (minimum 0).
- **Tooltip** on the text box and the drop-down: `In the ECU: {Original}`, where Original is formatted
  the same way from the ECU copy.
- **Changed outline**: a **text box** whose row is changed gets a 2 px `Accent` border. The drop-down
  has no changed marking (see Open questions). A row is changed when the settings edit lists a change
  whose name equals the row's key, ignoring case. The key is `name`, or `name[i]` for element i > 0.
- **Units** (column 3): the constant's units, size 10, `Faint`.
- **Uncertain mark** `?`: size 11, `Marker`, left margin 5. Shown when the row's condition could not be
  evaluated. Tooltip (exact):
  `This firmware did not say whether the setting applies to your tune, so it is shown rather than hidden.`

**Drop-down options**: the constant's option names, keeping only valid positions (padding spelled
INVALID, or empty, is dropped). Duplicate names are dropped, keeping the first. Picking a name stores
the position of the **first** entry with that exact name in the full, unfiltered list (exact,
case-sensitive match).

**When a value is committed**:

- Text box: when it loses focus. Enter does not commit.
- Drop-down: as soon as the selection changes.

**Accepting a value** (`TrySet`):

- Refused outright when there is no constant, no edit, or the row is not editable.
- Text: the settings edit stores it if it fits as ASCII within the field's width.
- Choice: the position as above.
- Number: the text must parse as a float in the current culture. The settings edit then accepts it
  when:
  - it is finite, AND
  - it is inside [Low, High] when a range is declared, OR it equals the value this setting held when
    first seen this session. That exception keeps an out-of-range sentinel already in the ECU
    writable.
- The edit records the value **as stored**, after encoding and rounding, not as typed.

**On refusal**, the box goes back to the stored value. The row's problem text becomes the reason below,
and the hint is set to it:

| Condition | Exact reason |
|---|---|
| no constant or no edit | `That cannot be set here.` |
| not editable | `This setting is not editable.` |
| Text with a character > 0x7F | `The firmware stores this as plain ASCII.` |
| Text otherwise | `That is longer than the {Columns} characters this field holds.` |
| Choice | `That is not one of the choices this firmware offers.` |
| Number does not parse | `That is not a number.` |
| Number, range declared | `{Format(n)} is outside {Format(Low)} to {Format(High)} {Units}`. There is no trailing period. `Format` is `F{Digits}` as above. If Units is empty, a trailing space remains. |
| Number, no range | `The firmware will not hold that value.` |

**On acceptance**: the problem text clears, the row redraws, and the whole page is re-evaluated.

**Page re-evaluation** (`Refresh`, run after every row change and whenever the page opens):

- Each row with a condition is evaluated against a lookup. The lookup tries, in order:
  1. the edited setting value;
  2. else the newest sample of a log channel with that name;
  3. else NaN.
  Derived expressions from the firmware are also resolved.
- Result **Hidden** → the row is hidden. **Unknown** → the row is shown with `?`. Otherwise it is shown.
- Each curve with a condition is shown only if the condition says show.
- Visible list:
  - every row that is visible and not NotShown;
  - a blank caption (empty label) is dropped when it is first or follows another blank caption;
  - trailing blank captions are removed.

---

### Saved-tune commands (Tools menu)

These four entries sit in the Tools menu between two separators. Their mnemonic letters are underlined.

| Item | Enabled when | Tooltip |
|---|---|---|
| `Save the tune to a file…` (S) | `CanSaveTune` = a tune in hand AND a layout AND not a placeholder | `Write every setting to a .msq — a backup, and the format TunerStudio reads` |
| `Open a saved tune…` (O) | always | `Open a .msq and its firmware's settings pages, with no ECU attached` |
| `Compare with a saved tune…` (m) | `HasEcuTune` | `Says which settings the file and the tune in hand disagree about` |
| `Restore a saved tune to the ECU…` (R) | `CanBurn` | `Writes the settings that differ, leaving alone everything the file does not mention` |

(The File menu's `Open tune…` and `Use the tune stored in the log` belong to log analysis, not to
Calibration. Dropping a `.msq` file on the window also goes to log analysis.)

#### Save

1. A save dialog opens: title `Save the tune`, filter `TunerStudio tune|*.msq`, default extension `.msq`,
   suggested name `{yyyy-MM-dd_HH.mm.ss}.msq` (the current time).
2. If the user confirms, Report(`SaveTuneToFile(path)`):

| Outcome | Text |
|---|---|
| no tune | `There is no tune to save.` |
| placeholder | `This is a firmware definition rather than a tune — every value in it reads as zero, so there is nothing worth saving.` |
| ok | `Saved {total bytes of all pages:N0} bytes of settings to {file name}.` |
| I/O or permission error | `Could not write {file name}: {error message}` |

The file carries the ECU signature, the build symbols, an empty comment, and the PC variables of the
file the tune came from, when there was one.

#### Open a saved tune

1. An open dialog opens: title `Open a saved tune`, filter `TunerStudio tune|*.msq|All files|*.*`, and the
   file must exist.
2. On OK: switch to the Calibration mode. If `OpenSavedTune(path)` succeeds, switch to the Settings half.
3. Report(`EcuTuneSummary`).

`OpenSavedTune`:

- Read the file. A file with an empty signature fails with
  `{file} does not say which firmware it is for, so there is no way to know what its numbers mean.`
- Pick the best definition. Scan every known definition file. Skip any that cannot be read, has no
  pages, or that the file "looks like another firmware" against. An exact signature match (ignoring
  case) always beats an inexact one. Among equals, the definition with more settings applied wins.
- If none fits: register the signature as wanted, and fail with
  `{file} is a tune for "{signature}", and no definition here fits it. Put its .ini in {definitions folder} and try again.`
- On success:
  - adopt the layout, tune, tables, interface, curves and derived channels, read with the file's own
    build symbols;
  - make a fresh settings edit;
  - set `TuneIsPlaceholder = false` and `TuneIsFromFile = true`;
  - clear the pages written, the open page, the open curves and the menu selection;
  - rebuild the table list and the menu.
  - Summary: `{file} — {tables} tables · {pages:N0} pages. {load summary}{against}`, where `against` is
    empty when the signatures match. Otherwise it is
    ` Read against {ini file name} ("{ini signature}"), which is not the "{file signature}" this tune was saved from.`
- Read error: `Could not read {file}: {message}`.
- Whatever the outcome, every write gate and visibility is refreshed.

#### Compare

1. An open dialog opens: title `Compare with a saved tune`, same filter.
2. On OK, Report(`CompareWithSavedTune(path)`). The file is laid over the tune in hand, so a setting
   missing from the file is not reported as a difference. The differences are kept in `TuneDifferences`,
   which nothing in this section shows.

| Outcome | Text |
|---|---|
| no tune or layout | `There is no tune to compare against. Connect to an ECU or open a tune first.` |
| both signatures known and different | `{file} is a tune for "{fileSig}" and this is "{ecuSig}". They are different firmwares, so comparing them says little.` |
| 0 differences, file complete | `{file} matches, setting for setting.` |
| 0 differences, file incomplete | `{file} matches every setting it carries. {load summary}` |
| n differences | `{n:N0} setting{s} differ from {file}: ` + the first three difference summaries joined with `; `, then `, and more.` when n > 3, or `.` otherwise |
| read error | `Could not read {file}: {message}` |

The difference summaries come from the core library.

#### Restore (two-step, safety-critical)

1. An open dialog opens: title `Restore a saved tune to the ECU`, same filter. Cancel stops here.
2. Report(`PlanRestore(path)`). Planning sends nothing.

| Outcome | Text |
|---|---|
| placeholder or from-file tune | `Read the ECU's own tune first. A restore is worked out against what the controller actually holds, and this is not that.` |
| not connected | `Not connected to an ECU.` |
| no tune | `No tune has been read.` |
| signatures differ | `{file} is a tune for "{fileSig}" and this ECU says it is "{ecuSig}". {plan summary}` |
| ok | `{file}: {plan summary}` |
| read error | `Could not read {file}: {message}` |

3. If no plan was made, stop. If the plan is empty, forget it and stop. The hint already carries the
   plan summary.
4. **Confirmation dialog**: caption `OpenLogViewer`, buttons OK/Cancel, warning icon, Cancel is the
   default. Text:

```
Restore {file name} to the ECU?

{differences:N0} settings would change:
    {summary 1}
    …up to 12 lines, each indented 4 spaces
    …and {differences − 12:N0} more        ← only when more than 12

{bytes:N0} bytes across {pages} page(s).{leftAlone}{warning}

Nothing is burned, so a power cycle undoes all of it.
Do this with the engine stopped.
```

   - `leftAlone`, when the file is missing settings:
     `\n\n{missing:N0} settings this firmware has are not in the file. They are left exactly as the ECU holds them.`
   - `warning`, when the signatures differ:
     `\n\nTHIS FILE IS FOR ANOTHER FIRMWARE.\nThe file says "{fileSig}" and the ECU says "{ecuSig}". Settings may not mean the same thing in both.`
   - Cancel → forget the plan.
5. OK → Report(`ApplyRestore()`). This re-checks the same refusals as planning, in this order:
   - from-file or placeholder;
   - `Nothing has been planned.`;
   - not connected;
   - no tune;
   - empty plan → `The ECU already holds this tune, so nothing was sent.`

   Then **every page in the plan is checked before the first byte is sent**. If any page has no write
   command, the answer is `This firmware declares no way to write page {n}, so nothing was sent.`

   Then each write is sent. After each one the tune and the settings edit accept the write, and its
   page is added to the pages written.

| Outcome | Text |
|---|---|
| ok | `Restored {settings:N0} setting{s} to the ECU — {bytes:N0} bytes across {pages} page{s}. It is running this now, and will forget it at the next power cycle unless you burn it.` |
| failure part way | `The restore stopped after {done} of {total} writes: {message} The ECU is now holding part of this tune and part of the old one. Nothing has been burned, so a power cycle puts back what was last burned.` |

   Whatever happens, the plan is forgotten afterwards, the settings edit is reconciled with the tune,
   the page is re-evaluated, the burn gates refresh, and the tables and curves on screen are re-read
   from the tune. The selected table is kept by name, or the first table is chosen.

   A restore does not burn. The settings `Burn` then offers to commit the restored pages.

---

### Send, burn and revert flows

Every confirmation below uses the caption `OpenLogViewer`, the buttons **OK / Cancel**, a warning icon,
and **Cancel as the default**. Only OK proceeds. Each result is Report()ed.

#### Send table (`OnWriteTableClick`)

The flow does nothing when there is no table edit. The confirmation text:

```
Send {n} changed cell{s} of {table name} to the ECU?

This takes effect immediately on a running engine.

It is not permanent: the ECU forgets it at the next power cycle unless you burn it.
```

n = the number of changed cells in the table. `WriteTableToEcu()` checks, in order:

| Check | Text |
|---|---|
| placeholder | `This is a firmware definition rather than a tune — every value in it reads as zero, so nothing here may be sent to a controller.` |
| from file | `This tune was opened from a file rather than read off the controller, so it cannot be sent back. Read the ECU's own tune first.` |
| no table | `No table is open.` |
| neither connection nor MaxxECU USB | `Not connected to an ECU.` |
| no tune or layout | `No tune has been read.` |
| no changes | `Nothing has been changed.` |
| cannot encode | `This table cannot be encoded for this firmware, so nothing was sent.` |
| (MaxxECU USB) | see below |
| page missing | `The firmware declares no page {p}.` |
| page has no write command | `This firmware declares no way to write page {p}.` |
| ok | `Sent {n} changed cell{s} to the ECU. It is running this now, and will forget it at the next power cycle unless you burn it.` |
| error | `The write failed: {message}` |

The connection reads every write back to check it. On success:

- the tune and the settings edit accept the write;
- the table is re-read from the tune and re-selected, which makes a fresh table edit, clears the
  changed marks and resets the selection to cell (0,0).

The send does **not** add to the pages written, so a table send does not light the settings Burn.

**MaxxECU USB table write**: one write at the encoded offset, then a read-back check.

| Outcome | Text |
|---|---|
| status not OK | `The write failed: The MaxxECU answered "{status}" for the write at offset {offset}.` |
| read-back differs | `The write failed: The MaxxECU took the write at offset {offset} but reads back something else now.` |
| ok | `Sent {n} changed cell{s} ({bytes:N0} bytes) to the MaxxECU, verified. There is no burn step on this controller — it is already running this, permanently, and a power cycle will not undo it.` + overlap warning |

The overlap warning is added when the written bytes overlap named settings:
` Warning: this table's cells share bytes with a named setting on this ECU ({up to 5 "names", comma-separated}{, and N more}) — the table may never have been sized here, and this write may have changed that setting's value along with the cell you asked for.`

#### Burn table (`OnBurnTableClick`)

The flow does nothing when there is no table edit. The confirmation text:

```
Burn the page holding {table name} to the ECU's flash?

This is permanent. A power cycle will not undo it.

Burn with the engine stopped: the ECU pauses while it writes flash.
```

`BurnTableToEcu()` checks, in order:

| Check | Text |
|---|---|
| no table | `No table is open.` |
| not connected | `Not connected to an ECU.` |
| no tune | `No tune has been read.` |
| cannot encode | `This table cannot be encoded for this firmware.` |
| no page | `The firmware declares no page {p}.` |
| page has no burn command | `This firmware declares no way to burn page {p}.` |
| ok | `Burned page {p}. This survives a power cycle.` |
| refused by the ECU | `The burn failed: {message}` |
| not answered (unconfirmed) | `{message}`, the protocol's own message, unchanged |

A burn commits the page as the ECU now holds it, including anything sent earlier. Unsent on-screen
edits are **not** included, and the confirmation does not say so. On success:

- the page is removed from the pages written;
- every write gate refreshes;
- a "burned tune" version is recorded (`KeepBurnedTune`, owned by another section).

#### Send settings (`OnWriteSettingsClick`)

The flow does nothing unless `CanWriteSettings`. The confirmation text:

```
Send {n} changed setting{s} to the ECU?

{bytes:N0} bytes across {pages} page{s}.

This takes effect immediately on a running engine.

It is not permanent: the ECU forgets it at the next power cycle unless you burn it.
```

`WriteSettingsToEcu()` checks:

| Check | Text |
|---|---|
| no settings edit | `No tune has been read.` |
| neither connection nor MaxxECU USB | `Not connected to an ECU.` |
| no layout or tune | `No tune has been read.` |
| no changes, or no writes | `Nothing has been changed.` |

It then sends each write. A missing page raises the error `The firmware declares no page {p}.` and a
page with no write command raises `This firmware declares no way to write page {p}.`

| Outcome | Text |
|---|---|
| ok | `Sent {n} setting{s} ({bytes:N0} bytes) to the ECU. It is running them now, and will forget them at the next power cycle unless you burn them.` |
| error before any write | `Nothing was sent: {message}` |
| error part way | `{done} of {total} writes reached the ECU ({bytes:N0} bytes) and then this failed: {message} Some settings have changed and some have not. Nothing was burned, so turning the key off restores all of it.` |

Each write that succeeds adds its page to the pages written. Whatever happens, the edit is then
reconciled: only the bytes that still differ stay pending. The page is re-evaluated and the gates
refresh.

This method does **not** itself refuse a placeholder or from-file tune. The button gate does
(see Open questions).

**MaxxECU USB settings write**: each write gets a status check and a read-back.

| Outcome | Text |
|---|---|
| ok | `Sent {n} setting{s} ({bytes:N0} bytes) to the MaxxECU, verified. There is no burn step on this controller — it is already running them, permanently, and a power cycle will not undo it.` |
| error before any write | `Nothing was sent: {message}` |
| error part way | `{done} of {total} writes reached the ECU ({bytes:N0} bytes), verified, and then this failed: {message} Those {done} are already applied and permanent — there is no burn to skip and no power cycle that undoes them.` |

The MaxxECU write never adds to the pages written.

#### Burn settings (`OnBurnSettingsClick`, also the curve bar's Burn)

The flow does nothing unless `CanBurnSettings`. The confirmation text:

```
Burn {pages} page{s} of settings to the ECU's flash?

This is permanent. A power cycle will not undo it.

Burn with the engine stopped: the ECU pauses while it writes flash.
```

pages = the number of pages written. `BurnSettingsToEcu()`:

- Refuses with `Not connected to an ECU.`, `No tune has been read.`, or
  `Nothing has been sent, so there is nothing to burn.`
- For each page written, in ascending order:
  - If the page has no burn command, remove it from the set and go on.
  - Otherwise burn it and remove it **as soon as it lands**.
  - An unconfirmed burn (no answer) is also removed, and then the error is raised. A burn the ECU
    refuses stays listed.

| Outcome | Text |
|---|---|
| none burnable | `This firmware declares no way to burn the pages that were written.` |
| ok | `Burned {k} page{s} to flash. These settings now survive a power cycle.` (and a version is recorded) |
| error, none burned | unconfirmed: `{message}`. Refused: `The burn failed: {message}` |
| error after some burned | `{k} page{s} were burned and then ` + (`this happened: ` when unconfirmed, `this failed: ` when refused) + `{message} The rest are in working memory and will be lost at the next power cycle.` |

#### Send curve (`OnSendCurveClick`)

The flow does nothing unless `CanWriteCurve`. moved = the number of changed points across all open
curves. what = ` of "{title}"` when exactly one curve is open, otherwise empty. The confirmation text:

```
Send {moved} moved point{s}{what} to the ECU?

It takes effect at once. Nothing is burned, so a power cycle undoes it.
```

`WriteCurveToEcu()` checks:

| Check | Text |
|---|---|
| placeholder | the placeholder refusal, as for the table |
| from file | the from-file refusal, as for the table |
| none open | `No curve is open.` |
| not connected (TunerStudio connection only) | `Not connected to an ECU.` |
| no tune | `No tune has been read.` |
| no changes | `Nothing has been changed.` |

It then **encodes every changed curve and checks every page before sending anything**:

- a curve that cannot be encoded: `"{title}" cannot be encoded for this firmware, so nothing was sent.`
- no writes: `Nothing has been changed.`
- a page with no write command: `This firmware declares no way to write page {p}, so nothing was sent.`

Both rows of a curve (X and Y) go out together.

| Outcome | Text |
|---|---|
| ok | `Sent {moved} moved point{s} to the ECU. It is running this now, and will forget it at the next power cycle unless you burn it.` |
| error | `The write failed: {message}` |

Each write is accepted into the tune and the settings edit, and its page is added to the pages written.
That is what lights `Burn`. Success or failure, the curves are rebuilt from the tune, which clears the
moved marks.

---

### State rules

#### Gate definitions (exact)

| Gate | Rule |
|---|---|
| `HasEcuTune` | tune in hand ≠ null |
| `ShowNoTuneNotice` | no tune AND NOT `IsObd2Live` |
| `HasSettingsPages` | the settings menu has ≥ 1 entry |
| `HasTableChanges` | table edit exists AND some cell changed |
| `CanWriteTable` | `HasTableChanges` AND `_ecuConnection` ≠ null AND layout ≠ null AND NOT placeholder AND NOT from file |
| `CanBurn` | `_ecuConnection` ≠ null AND layout ≠ null AND NOT placeholder AND NOT from file. It does **not** need changes. |
| `HasSettingChanges` | settings edit has changes |
| `CanWriteSettings` | `HasSettingChanges` AND `_ecuConnection` ≠ null AND layout ≠ null AND NOT placeholder AND NOT from file |
| `CanBurnSettings` | pages written ≥ 1 AND `_ecuConnection` ≠ null AND NOT placeholder AND NOT from file |
| `HasCurveChanges` | some open curve has a changed point |
| `CanWriteCurve` | `HasCurveChanges` AND `_ecuConnection` ≠ null AND layout ≠ null AND NOT placeholder AND NOT from file |
| `CanSaveTune` | tune ≠ null AND layout ≠ null AND NOT placeholder |
| `CanApplyRestore` | plan exists and is not empty AND `_ecuConnection` ≠ null AND NOT placeholder AND NOT from file |

All of these gates must be re-evaluated (`RaiseWriteGates`) on:

- connect;
- disconnect;
- tune read;
- opening a file or a definition;
- every burn;
- every edit that can change them.

They depend on the connection, which nothing else observes. A gate that is not re-evaluated leaves a
button stuck in its old state.

#### Per-state behaviour

| State | What the user sees and can do |
|---|---|
| **No tune** | The no-tune notice, or the fault panel when OBD2 is live. No lists, no work area. Save, Compare and Restore are disabled; Open a saved tune works. |
| **Placeholder (definition opened, scripted only)** | Lists and pages are shown with every value zero. Summary: `{ini file} — {tables} tables · {pages:N0} pages. ` + `No ECU is connected, so every value reads as zero.` (when not connected) or `Every value reads as zero — this is what the firmware offers, not what the attached ECU is set to, so nothing here can be sent to it.` (when connected). The other summaries: `{ini file} declares no pages of settings.` when it has none, or `Could not read {ini file}: {message}` on error. Editing works; every Send, Burn, Save and Restore is disabled or refused. |
| **Tune from file, no ECU or ECU attached** | Everything can be viewed and edited, and Save and Compare work. Every Send and Burn is disabled. Restore is disabled. If clicked through a script, planning refuses with `Read the ECU's own tune first. …`. A connect replaces the file tune with the ECU's (see Lost changes). |
| **Connected, tune read** | Summary: `{total:N0} bytes read from the ECU in {ms:N0} ms · {tables} tables · {settings:N0} settings`, plus ` · {dialogs:N0} pages` when the interface has dialogs. On read failure: `The ECU's settings could not be read: {message}`. The first (largest) table is selected. Send and Burn follow the gates. |
| **MaxxECU USB, tune read** | Summary: `{bytes:N0} bytes read from the ECU · {tables} tables · {settings:N0} settings`. No curves. The tables and settings are shown, but `_ecuConnection` is null, so every Send/Burn button and Restore is **disabled** (see Open questions). |
| **Read-only** | There is no read-only switch in this mode. Fields are read-only only by the firmware's word: the item is a read-only field, or the constant is not on the controller. The agent's write permission belongs to another section. |
| **Unsent changes** | Tables: changed cells are outlined and the summary says `{n} cells changed, not sent`. Settings: changed text boxes are outlined and the summary says `{n} settings changed · {bytes} bytes to send`. Curves: moved points are filled, the original line is dashed, and the summary says `… moved, not yet sent`. |
| **Unburned changes** | Only settings and curve sends are tracked, through the pages written, which lights the settings and curve `Burn`. Table sends are not tracked: the table `Burn` is lit whenever connected, whether or not anything was sent. No UI marks unburned table writes. |
| **Disconnected after reading** | The tune stays on screen and edits are kept. Every Send and Burn turns disabled because the connection is gone. The pages written are kept, but they cannot be burned until a new connect, which clears them. |
| **Lost changes (silent)** | Unsent table edits are thrown away **without warning** when another table is selected, including through a settings menu entry. They are also lost on a successful send, by design. Unsent table, settings and curve edits are all lost without warning on: reconnect or a fresh tune read, opening a saved tune, opening a definition, and a restore (which re-reads the tables and curves). Pages written but not burned are forgotten when a new tune is read or opened. The ECU then loses them at the next power cycle. |

---

### Tune summary texts (`EcuTuneSummary`)

| Situation | Text |
|---|---|
| ECU read, TunerStudio | `{total:N0} bytes read from the ECU in {ms:N0} ms · {tables} tables · {settings:N0} settings` and optionally ` · {dialogs:N0} pages` |
| ECU read failed | `The ECU's settings could not be read: {message}` |
| MaxxECU | `{bytes:N0} bytes read from the ECU · {tables} tables · {settings:N0} settings` |
| Saved tune | see "Open a saved tune" |
| Definition | see the Placeholder row |

---

### Open questions

1. **Keyboard nudge ignores the "by" box.** The `+`/`−` keys add the firmware Step, or ×10 with Shift.
   The `+`/`−` buttons add the value in the "by" box. A code comment says the keys and buttons are "the
   same request". Spec ambiguity; the user decides which is intended.
2. **`+` on the main keyboard needs Shift**, so it always nudges ×10, while `−` nudges ×1. Is this
   intended? The user decides.
3. **No live operating-point marker or trail** exists on the WPF tune table. The brief expected one.
   The user decides whether the Rust version adds it.
4. **No cell typing, Enter, Tab, Delete, Ctrl+C/V or undo** on the table. Values are set only through
   `Set to`. The user decides whether the Rust version adds any.
5. **MaxxECU USB cannot send from the UI.** Every Send/Burn gate requires `_ecuConnection`, which a
   MaxxECU USB session never sets. Yet `WriteTableToEcu` and `WriteSettingsToEcu` have MaxxECU branches,
   probably reached only by agent or scripted calls. The user decides (safety-relevant: MaxxECU writes
   are permanent).
6. **Restore menu item is gated on `CanBurn`, not on a restore-specific rule.** The planning step
   refuses the wrong states anyway. A builder can keep it.
7. **Table Burn is enabled with nothing sent and nothing changed**, and its confirmation does not
   mention unsent on-screen edits, which are not burned. The user decides.
8. **Table sends do not mark the page as written**, so after a table send the settings Burn stays dark
   and only the table Burn commits it. The user decides whether to unify this.
9. **A table opened from a settings menu entry** shows the editable table without its tools, preview,
   key hint, or Send/Burn/Revert buttons, because those are tables-half only. Its edits cannot be sent
   from there and are lost when another table is chosen. The user decides.
10. **Silent loss of unsent edits** when switching tables, reconnecting or opening a file. There is no
    "discard changes?" prompt. The user decides.
11. **The drop-down (Choice) rows have no changed outline**, and changed text boxes use `Accent` while
    changed table cells use `Marker`. The builder can settle whether to match; the user decides the colour.
12. **Settings menu entry conditions are never evaluated**, so every page is listed even when the
    firmware says it does not apply. The user decides.
13. **`EcuTableSummary` (`N tables`) is shown in the settings half too.** A builder can settle this.
14. **The heat extent comes from the edited values**, so colours shift while editing. Probably intended;
    a builder keeps it.
15. **Table edit values are not snapped to the storage step** until encoding. The preview and cells can
    show values the ECU will round. Settings, by contrast, record the stored value. A builder keeps the
    WPF behaviour unless the user says otherwise.
16. **`WriteSettingsToEcu` itself does not refuse a placeholder or from-file tune.** Only the button gate
    stops it. The table and curve writes do refuse. Safety-relevant for scripted and agent callers; the
    user decides (recommend adding the refusal).
17. **Curve keyboard works only while the pointer hovers a point.** Focus comes only from pressing on a
    point. A builder can settle this.
18. **The settings text box commits only on focus loss**, not on Enter. A builder can settle this.
19. **The nudge and scale boxes accept 0 or negative values.** Text that is not a number shows a
    validation error and keeps the old value. A builder can settle this.
20. **The fault panel and a file-opened tune can both be visible** if a tune file is opened during a
    live OBD2 session. A builder can settle this.
21. **The hint says "Esc undoes the selection"** when Esc actually reverts the selected cells. The text
    must be kept exactly; flagged for wording only.
22. **Restore confirmation uses a literal `page(s)`**, while the other messages pluralise. Keep it exactly.

---

### Safety rules found

| # | Trigger | Enabled rule | Confirmation (exact; caption `OpenLogViewer`, OK/Cancel, warning, default Cancel) |
|---|---|---|---|
| S1 | Tables half `Send to ECU` | `CanWriteTable` | `Send {n} changed cell{s} of {table} to the ECU?\n\nThis takes effect immediately on a running engine.\n\nIt is not permanent: the ECU forgets it at the next power cycle unless you burn it.` |
| S2 | Tables half `Burn` | `CanBurn` (no change needed) | `Burn the page holding {table} to the ECU's flash?\n\nThis is permanent. A power cycle will not undo it.\n\nBurn with the engine stopped: the ECU pauses while it writes flash.` |
| S3 | Settings half `Send to ECU` | `CanWriteSettings` | `Send {n} changed setting{s} to the ECU?\n\n{bytes:N0} bytes across {pages} page{s}.\n\nThis takes effect immediately on a running engine.\n\nIt is not permanent: the ECU forgets it at the next power cycle unless you burn it.` |
| S4 | Settings half `Burn` and curve-bar `Burn` | `CanBurnSettings` | `Burn {pages} page{s} of settings to the ECU's flash?\n\nThis is permanent. A power cycle will not undo it.\n\nBurn with the engine stopped: the ECU pauses while it writes flash.` |
| S5 | `Send curve to ECU` | `CanWriteCurve` | `Send {moved} moved point{s}{ of "title"} to the ECU?\n\nIt takes effect at once. Nothing is burned, so a power cycle undoes it.` |
| S6 | Tools → `Restore a saved tune to the ECU…` | menu: `CanBurn`. Apply: plan not empty, connected, not placeholder, not from file. | Two steps: pick a file, plan (nothing sent), then the multi-line confirmation in "Restore". It lists up to 12 settings, the bytes and pages, the left-alone count, `THIS FILE IS FOR ANOTHER FIRMWARE.` when the signatures differ, and ends `Nothing is burned, so a power cycle undoes all of it.\nDo this with the engine stopped.` |

Further rules:

- **Placeholder and from-file tunes can never be sent, burned or restored.** The table, curve and
  restore methods refuse in their own code. The settings write relies on its gate (Open question 16).
- **Check before sending:**
  - Restore and curve sends check every page's write command before the first byte is sent.
  - Table and settings sends check each page as they reach it.
- **Read-back:**
  - TunerStudio connection writes are read back and compared by the connection.
  - MaxxECU writes are read back explicitly.
- **Burn bookkeeping:**
  - A settings burn commits only the pages written, striking each one off as it lands.
  - An unconfirmed burn is also struck off, so it is never erased twice.
  - A refused burn stays listed.
- **No burn on MaxxECU.** Its writes are permanent at once, and the result text says so.
- **Partial failures are reported as partial**, naming how many writes landed and what a power cycle
  does.
- **Disconnect** clears the connection, which disables every Send and Burn. It also clears the agent's
  write permission (owned by another section).
- **Edits stay local.** Every edit (keys, tools, drag, settings fields) changes only the on-screen copy.
  Nothing reaches the ECU without one of the confirmations S1 to S6. A scripted cell nudge
  (`ActivateTuneCell`) is local only, and a scripted restore only plans and then forgets the plan.
- **Drag guards that must be kept:**
  - A curve drag ends at once if the mouse capture is lost or the button is up, so no stray movement
    rewrites a point.
  - Both axis spans are frozen for the length of a drag, so the value cannot creep upward.
  - A breakpoint can never pass its neighbours.
- **Clamping:**
  - Table and curve values are clamped to the firmware's declared range.
  - Settings outside the range are refused, except a value equal to the one first seen this session.


## §5 `w7-gauges-faults`: Gauges mode and fault codes

### Scope

Package **w7-gauges-faults** owns two things:

1. **Gauges mode** - the live dashboard of dials (one workspace mode of the main window), its gauge picker and its empty state. Rust module `olv::gauges`.
2. **Fault codes** - the OBD2 trouble-code (DTC) panel, which appears in two hosts: as the Calibration tab's content while an OBD2 vehicle is live, and as a separate "Fault codes" window opened from the Tools menu. One panel implementation serves both hosts; the hosts differ only in when they scan and whether a Close button is shown.

The data model rules (gauge spec, band classification, unit conversion, fault scan/clear) are given here in full because the drawing depends on them exactly.

---

### Gauge data model

#### GaugeSpec (one dial definition)

| Field | Meaning | Default |
|---|---|---|
| `Name` | the INI's gauge configuration name, e.g. `CLTGauge`; identity key for keeping the dashboard across rebuilds | required |
| `Channel` | output channel the gauge displays (firmware's internal name) | required |
| `Title` | text drawn at the top of the dial | required |
| `Units` | units label (may be empty) | `""` |
| `Category` | group heading in the picker | `""` |
| `Low`, `High` | scale ends | 0, 0 |
| `LowDanger`, `LowWarning`, `HighWarning`, `HighDanger` | limits | NaN |
| `ValueDigits` | decimals on the reading and on the peak/low figures | 0 |
| `LabelDigits` | decimals on the two scale-end labels | 0 |

Derived rules (must be exact - the face and the reading colour use the same rules):

- `HasScale` = `High > Low`.
- `HasBands` = `HasScale` AND none of the four limits is NaN AND `LowDanger <= LowWarning <= HighWarning <= HighDanger`.
- `Inside(limit)` = `limit > Low && limit < High` (strict on both ends). A limit on or beyond either scale end means "no limit at that end".
- `Fraction(v)`: 0 if v is NaN or `High <= Low`; else `clamp((v - Low) / (High - Low), 0, 1)`.
- `BandFor(v)`:
  1. NaN -> `Unknown`.
  2. not `HasBands` -> `Normal`.
  3. `Inside(LowDanger) && v <= LowDanger` -> `Danger`.
  4. `Inside(HighDanger) && v >= HighDanger` -> `Danger`.
  5. `Inside(LowWarning) && v <= LowWarning` -> `Warning`.
  6. `Inside(HighWarning) && v >= HighWarning` -> `Warning`.
  7. otherwise `Normal`.
- `BandEdges()` (positions along the dial 0..1, for painting) =
  `(Fraction(Inside(LowDanger) ? LowDanger : Low), Fraction(Inside(LowWarning) ? LowWarning : Low), Fraction(Inside(HighWarning) ? HighWarning : High), Fraction(Inside(HighDanger) ? HighDanger : High))`.
- `In(unitSystem)`: if the units do not convert for that system, return the spec unchanged; otherwise a copy with `Units` relabelled and `Low`, `High` and all four limits converted with the same value conversion (NaN stays NaN).

#### Where specs come from (per connection type)

| Connection | Gauges listed (AllGauges) | Starts on the dashboard | Summary "scales from" text |
|---|---|---|---|
| INI firmware (Speeduino, rusEFI, MegaSquirt...) | every entry in the INI's `[GaugeConfigurations]` section, file order; `gaugeCategory = "X"` lines set the category for following entries; a name defined twice takes the later definition in the earlier position | the names already on the dashboard before a rebuild (in their order); if none, `[FrontPage]` `gauge1..gaugeN` in numeric order; in both cases only gauges that are *connected* (have a feeding column) and exist | the project tune name (`_projectTuneName`) if non-empty |
| OBD2 | one per channel of each parameter the car reported; `Category = "OBD2 · {pid}"`; `LabelDigits = Digits > 1 ? 1 : Digits` | the fixed front page `RPM, Speed, Throttle Position, Engine Load, MAP, Coolant, IAT, Battery` (those that exist) | `the OBD2 standard, with typical limits` |
| MaxxECU | from MTune's channel definitions for the subscribed channels | every connected one | `MTune's channel definitions`, or none when MTune's definitions failed (then the main hint shows the problem text, below) |
| SSM (Subaru) | one per parameter in the user's address file; Title = Name = Channel = parameter name; `Category = "SSM · 0x{Address:X6}"`; no limits (no bands); `LabelDigits = Digits > 1 ? 1 : Digits` | all of them | `the addresses in {SsmParameterFile.Name}` |

INI gauge line parsing: fields split on commas not inside quotes or `{}`; fewer than 9 fields -> entry skipped. Fields: channel, title, units, low, high, lowDanger, lowWarning, highWarning, highDanger, [valueDigits], [labelDigits]. A bound is a plain invariant-culture number or a `{expression}` evaluated against the tune settings (ECU tune scalars + opened tune file or project tune); unresolvable -> NaN. If either `low` or `high` is NaN, both become 0 (so the gauge has no scale). Digits parse as integer clamped 0..6, else 0. A units field that is a `{...}` expression shows as no units. Quotes are stripped.

MaxxECU problem hints (set on the main window hint line):
- `MTune is not installed, so there are no ranges to draw dials over.`
- `MTune's channel definitions could not be read: {exception message}`

#### Column lookup (which live column feeds a gauge, INI path)

A gauge's channel is the firmware's internal name; live sessions record the datalog label. Build a map from each datalog entry's channel to its label (label if non-empty, else the channel). Look up the gauge channel; if missing, follow the INI's alias map (`MsqIni.ReadAliases`) up to 4 hops; nothing found -> column is null and the gauge is **not connected** (it is listed in the picker but its tick box is disabled and it can never be on the dashboard). OBD2, MaxxECU and SSM gauges use their own channel name as the column.

#### GaugeItem (one gauge on screen)

State: source spec (as reported), displayed spec (`source.In(unitSystem)`), column, latest raw value (NaN at start), peak and trough raw (NaN at start), shown flag.

- Readings are stored raw (ECU units) and converted on the way out: `Value`, `Peak`, `Trough` each = `UnitConvert.Value(raw, source.Units, system)`.
- `Record(reading)`: sets the value; if the reading is NaN stop; else if peak is NaN or reading > peak, peak = reading; if trough is NaN or reading < trough, trough = reading.
- `ResetPeaks()`: peak and trough back to NaN; current value untouched.
- Changing unit system re-faces the dial and re-expresses value, peak and trough together.
- `Retarget(spec)`: replace the source spec, keep readings and extremes.
- `Category` shown as `Other` when empty. Search text = `"{Title} {Channel} {Category}"`.

#### Unit conversion (display only; recordings keep ECU units)

Units are recognised after removing `°`, space, `.`, `_`, tab and lowercasing:

| Recognised as | Spellings |
|---|---|
| Celsius | `c`, `degc`, `degreesc`, `celsius`, `centigrade` |
| Fahrenheit | `f`, `degf`, `degreesf`, `fahrenheit` |
| km/h | `km/h`, `kmh`, `kph`, `kmph`, `kilometresperhour` |
| mph | `mph`, `mi/h`, `milesperhour` |
| kPa | `kpa`, `kilopascals`, `kilopascal` |
| psi | `psi`, `lbf/in2`, `poundspersquareinch` |

Conversions: Celsius->Imperial `v*9/5+32`, label `°F`; Fahrenheit->Metric `(v-32)*5/9`, label `°C`; km/h->Imperial `v/1.609344`, label `mph`; mph->Metric `v*1.609344`, label `km/h`; kPa->Imperial `v/6.894757293168361`, label `psi`; psi->Metric `v*6.894757293168361`, label `kPa`. Everything else (including "As reported") is unchanged. A spec "converts" only when the label would change.

---

### Gauges mode layout

Shown when the workspace mode is Gauges (`MainViewModel.InGaugeMode`). Entered by: View menu item `_Gauges` (checkable, tooltip `Live dials, as the firmware defines them`), the toolbar mode chip `Gauges` (same tooltip; one of Log / Gauges / Calibration / Guide, mutually exclusive), or the command-line flag `--gauges`. No keyboard shortcut.

Three columns across the whole content area:

| Column | Width | Content |
|---|---|---|
| left | 260 px initially, min 180 | the gauge picker panel |
| splitter | 4 px, colour `Line` | drag to resize left/right columns |
| right | rest, min 300 | the dashboard grid (and the empty-state message overlaid on it) |

#### Gauge picker (left column)

Background `Panel`, 1 px `Line` border on its right edge only. Inner margin 10 left, 10 top, 6 right, 10 bottom. Stacked:

1. **Search box** at the top, full width, 4 px right margin, 6 px below it. Tooltip `Search by name, channel or category`. Applies 120 ms after typing stops. Filter: case-insensitive substring match of the search text in the item's search text (`"{Title} {Channel} {Category}"`); empty search shows all. Filters only the picker list, never the dashboard. No Enter/Esc handling.
2. **Gauge list** fills the middle; vertical scrolling only (no horizontal scroll; long titles trim with an ellipsis). No selection highlight (items are drawn bare). Grouped by `Category` - groups appear in order of the first gauge of each category in the list. Group header: category text, font 10, semibold, `Muted`, margin 8 above, 3 below. Each item: a tick box with the gauge `Title` as its label, 2 px vertical margin, tooltip = the gauge's `Channel` name. Tick state = whether the gauge is on the dashboard; **disabled** when the gauge is not connected (no feeding column).
3. **`Clear peaks` button**, full width, 6 px above, 4 px right margin. Tooltip `Forget the high and low markers on every dial`. Always enabled. Action: reset peaks and troughs on every gauge (shown or not) and set the main hint to `Peak and low markers cleared.`
4. **Summary line** at the very bottom, 6 px above, font 11, `Muted`, wraps. Text (`MainViewModel.GaugeSummary`):
   - no gauges at all: `Connect to an ECU to see its gauges.`
   - otherwise parts joined with ` · `:
     - `{shown} shown · {available} available` (shown = dashboard count; available = number of connected gauges)
     - if a scales source is named: `scales from {source}`
     - if any dashboard gauge has no scale: `{n} without a scale — open the tune that sets them`

#### Adding and removing gauges

- Ticking a box adds the gauge; unticking removes it. Any change rebuilds the dashboard in place: remove every dashboard item no longer ticked; then, walking the ticked gauges in picker (catalogue) order with index i, any not yet present is inserted at position `min(i, dashboard count)`. Existing dials keep their position (they are not re-created, so their peaks survive). Summary recomputed.
- The initial dashboard order is front-page order (or the kept order), which can differ from catalogue order; inserts after that follow the rule above. **Open question:** after the first manual add the order is a mix of front-page order and catalogue order; the comment says "rebuilt in catalogue order". Builder can settle (reproduce the stated insertion rule exactly).
- There is no drag-to-reorder, no context menu, no remove button on a dial.

#### Persistence

- The chosen dashboard is **not saved to settings** and does not survive an application restart.
- It survives a rebuild of the INI gauge list (a tune opened or changed while the INI is known): the names on the dashboard are kept in their order. If the dashboard was empty at that moment, it falls back to the firmware front page.
- OBD2, MaxxECU and SSM connections always start from their own defaults (table above); a new connection of any kind replaces the whole gauge list.

#### When the gauge list is rebuilt

- On connecting to an INI firmware (after the ECU tune is read).
- On any tune change (`TuneChanged` -> rescale): rebuilds from the last INI seen, if the last gauge source was an INI. Rebuilding creates new gauge items, so **all readings, peaks and lows are lost** (dials show "—" until the next sample). **Open question:** the comment on `Retarget` says peaks are kept, but a tune change re-seeds rather than retargets - builder can settle by matching the C# (peaks lost) or keeping them; user decision if it matters.
- On connecting OBD2, MaxxECU or SSM.
- The INI is remembered after disconnect, so opening a tune after disconnecting rebuilds the (now unfed) gauges too.

#### Feeding readings

- Each live refresh tick that has at least one sample: for every gauge **on the dashboard** only, record the newest sample of its column (or NaN if the column is missing in the snapshot). Gauges not on the dashboard record nothing (their extremes do not advance while hidden).
- The same newest sample is re-recorded each tick even if no new sample arrived (harmless for extremes).
- Disconnect does not clear the gauges: the dials stay with their last reading, peaks and lows (frozen). The empty-state message does not return.
- Opening a log file never seeds or feeds gauges (gauges are live only).

#### MaxxECU rev-counter auto-scale

For OBD2, MaxxECU and SSM connections a one-shot flag is armed (INI connections: never). On each feed tick while armed:

1. Find gauges whose `Spec.Name` is exactly `Rev limit` and exactly `RPM` (case-sensitive). Either missing -> try again next tick.
2. If the Rev limit gauge's displayed value is NaN or outside 1000..20000 -> try again next tick.
3. `top = ceil((limit + 1000) / 500) * 500`. Retarget RPM to its current spec with `High = top`, `LowDanger = 0`, `LowWarning = 0`, `HighWarning = limit - 500`, `HighDanger = limit`. Disarm.

**Open question:** the Rev limit gauge only gets a value while it is on the dashboard, so unticking it stops the rescale. Builder can settle (feed it regardless) or match.

#### Dashboard grid (right column)

- A vertical-scrolling area (scrollbar only when needed; never horizontal), inner padding 10 px.
- Dials laid out left to right, wrapping to a new row when the next one does not fit. Each dial is fixed **160 wide x 180 high** with a 5 px margin all round (170 x 190 cell). Dials never scale with the window; resizing changes only how many fit per row.
- Dials have no mouse or keyboard interaction, no hover readout, no tooltip, no context menu.

#### Empty states

| State | What the right column shows | Picker |
|---|---|---|
| no gauge list (never connected this run, or a log only) | centred text, `Muted`, centre-aligned, two lines: `Connect to an ECU to see its gauges.` / `The firmware supplies the dials, their ranges and their limits.` (visible exactly when `MainViewModel.NoGauges` = gauge list empty) | empty list; summary `Connect to an ECU to see its gauges.` |
| connected, gauges exist, none ticked | empty grid, no message | summary `0 shown · {n} available ...` |
| connected, before the first sample | dials with no needle and reading `—` | normal |
| disconnected after a session | last readings frozen on the dials | normal |

**Open question:** "connected but none shown" has no on-canvas hint. Builder may add one; matching the C# means none.

---

### Dial drawing (GaugeView)

Custom-drawn; repainted whenever spec, value, peak, trough or theme changes. Sizes below are logical pixels; all text uses the theme's UI font at semibold weight, current culture, left-to-right.

Colours (theme tokens): face `Card`; title, scale labels, units, extremes text, "no range set", normal hold tick `Muted`; track `Grid`; normal band `Nominal`; warning band/tick/reading `Warning`; danger band/tick/reading `Danger`; needle and hub `Text`; normal reading `Readout`. On a theme change all brushes are rebuilt and the dial repaints.

#### Geometry

- Nothing at all is drawn if there is no spec, or the dial is narrower or shorter than 40 px.
- Angles: position f (0..1) maps to angle `θ = (-135 + f*270 - 90)` degrees, point = `(cx + r*cos θ, cy + r*sin θ)` with y downward. f=0 is lower left (7:30 o'clock), f=0.5 is straight up, f=1 is lower right (4:30). Sweep is clockwise, 270°, the gap at the bottom. An arc from a to b uses the large-arc flag when `(b-a)*270 > 180`.
- Strips from top: title 18, dial (`height - 18 - 30 - 14`), reading 30, extremes 14.
- `radius = min((width - 28) / 2, (dialStrip - 14) / 1.707)`; centre = `(width / 2, 18 + radius + 4)`.
- At the fixed 160 x 180 size: dialStrip 118, radius ≈ 60.93, centre ≈ (80, 82.93).

#### Draw order

1. **Face**: rounded rectangle over the whole bounds, corner radius 8, fill `Card`, no outline.
2. **Title**: `Title`, font 11, `Muted`, single line, max width `max(20, width - 8)`, trimmed with a character ellipsis, centred horizontally, top at y = 3.
3. If `radius > 10` AND `HasScale`:
   1. **Track**: full arc 0..1, `Grid`, stroke 7, round caps.
   2. **Bands** (stroke 7, round caps). Each band is skipped if `to - from < 0.004`, and is clamped to 0..1. With `HasBands`, using `BandEdges()` = (ld, lw, hw, hd), painted in this order: `Nominal` lw..hw; `Warning` ld..lw; `Warning` hw..hd; `Danger` 0..ld; `Danger` hd..1. Without bands: one `Nominal` arc 0..1.
   3. **Scale-end labels**: `Low` and `High` formatted `F{LabelDigits}` (current culture), font 9, `Muted`. Anchored at radius + 8 on the arc ends: the low label's right edge at the f=0 point, vertically centred on it; the high label's left edge at the f=1 point, vertically centred.
   4. **Peak tick** then **trough tick** (each skipped if NaN): radial line from radius+1 to radius+6 at `Fraction(value)` (clamped to the face), stroke 2, round caps, coloured by `BandFor(value)`: Danger -> `Danger`, Warning -> `Warning`, else `Muted`.
   5. **Needle** (skipped if value NaN): line from 0.18·radius to 0.92·radius at `Fraction(value)`, `Text`, stroke 2.5, round caps; then a filled `Text` circle of radius 3 at the centre. A value beyond the scale pins the needle at that end.
4. **Reading**: top = `height - 30 - 14` when the dial was drawn (has scale and radius > 10), otherwise `(height - 30) / 2` (vertically centred). Text = `—` (em dash) if value NaN, else value formatted `F{ValueDigits}` (current culture), font 19, colour by `BandFor(value)`: Danger -> `Danger`, Warning -> `Warning`, Normal/Unknown -> `Readout`. Units (if non-empty): `" " + Units`, font 10, `Muted`, placed immediately right of the number, its top at `top + numberHeight - unitsHeight - 2` (bottom-aligned with the number, 2 px up). The number+units pair is centred horizontally as one block.
   - Out-of-range values are printed as-is (not clamped) and coloured by the band rules (e.g. a value above `HighDanger` that is inside the scale reads `Danger`).
5. **No scale note**: if not `HasScale`, the text `no range set`, font 9, `Muted`, centred horizontally, top at `readingTop + 24`. (Such a gauge shows title, reading and note, no arc.)
6. **Extremes figures** (skipped when both peak and trough are NaN): text `▾{low}   ▴{high}` (three spaces between) where each is formatted `F{ValueDigits}` or `—` when NaN; font 9, `Muted`, one line, max width `max(20, width - 6)`, centred horizontally, bottom 2 px above the dial's bottom edge.

Note: .NET `F` formatting can print `-0` / `-0.0` for small negatives; the Rust formatter is .NET-compatible, so keep that behaviour.

---

### Fault codes panel

Used in two hosts (below). It never scans on its own except as described per host.

#### Layout

Whole panel background `Bg`. Top to bottom, the content scrolls vertically (scrollbar when needed) with 16 px margin; a fixed **button bar** is docked at the bottom.

**Button bar** (bottom): background `Panel`, 1 px `Line` border on its top edge, padding 12 horizontal, 7 vertical. Right-aligned from the right edge: `Close` (80 wide; only in the window host), 8 px gap, `Erase codes…` (110 wide), 8 px gap, `Scan` (90 wide). The buttons keep their width at any window size. The remaining space to their left holds the **status text**: font 11, `Muted`, vertically centred, trimmed with an ellipsis.

**Headline box** (first in the scroll area): padding 14 horizontal, 11 vertical, corner radius token `CornerMedium`, background `Panel`, 1 px `Line` border. Inside, stacked:
- Headline: font 15, semibold, `Text`, wraps.
- Link line: font 11, `Muted`, wraps, 5 px above.
- Warning: font 11, colour `Marker`, wraps, 8 px above; hidden when empty.

**Three sections**, each hidden entirely (heading, explanatory note and list) when it has no codes:

| Section | Heading text (font 11, semibold, `Muted`, margin 16 above, 6 below) | Note (font 11, `Muted`, wraps, 6 below) |
|---|---|---|
| Stored (confirmed, mode 03) | `CONFIRMED — THESE ARE WHAT LIT THE LAMP` | none |
| Pending (mode 07) | `PENDING — SEEN ONCE, NOT YET CONFIRMED` | `Most monitors want the same fault on two consecutive drive cycles before they will store it. A pending code is either the beginning of a real fault or a one-off that will clear itself.` |
| Permanent (mode 0A) | `PERMANENT — THESE CANNOT BE ERASED` | `Only the controller may clear these, and it will not do so until it has watched the monitor pass on its own. They exist so that a car cannot be presented for an emissions test with its faults wiped minutes earlier.` |

**Footer note** (always shown), font 11, `Muted`, wraps, 18 px above:
`A code is an observation and not a diagnosis. "System too lean, bank 1" is what the car measured; the cause is anything from a split hose to a tired pump, and the number cannot tell you which. Where a description is missing the code belongs to the vehicle manufacturer, who assigns those ranges freely — the same five characters mean unrelated things on two different marques, so guessing at one would be worse than saying nothing.`

**Fault row** (one per code, in the order the car reported them; codes from several modules are all kept): a card with padding 10 horizontal / 8 vertical, 4 px below, corner radius token `Corner`, background `Panel`, 1 px `Line` border. Left: the code (e.g. `P0133`) in the `DataFont` family, size 15, semibold, `Accent`, fixed 76 px wide, top-aligned. Right of it, stacked: the detail text (`Text`, wraps), then the "where" line (font 11, `Muted`, wraps, 3 px above).

- Detail = the standard's description if one is known; else for a manufacturer code `Defined by the vehicle manufacturer — the standard does not assign this number, so it has to be looked up against the marque.`; else `In the standard's range, but not in the list carried here. Worth looking up.`
- Where = generic: `{System} · defined by SAE J2012 — the same on every vehicle`; manufacturer: `{System} · the vehicle manufacturer's own number`. System = `Powertrain` (P), `Chassis` (C), `Body` (B), `Network` (anything else, i.e. U); empty for a malformed code.
- Authority: P0xxx and P2xxx generic; P3000–P3399 manufacturer, P3400+ generic; other P manufacturer; C/B/U with second char `0` or `3` generic, else manufacturer; a malformed code is manufacturer.

No row is clickable; no context menus, no tooltips, no keyboard shortcuts in the panel (Tab/Space/Enter on focused buttons only).

#### Drawing a result

When there is **no result** (never scanned, scan failed, or no OBD2 link):
- Headline `Nothing was read.`
- Link: if `MainViewModel.IsObd2Live` (OBD2 source present AND live): `The adapter is connected but did not answer.`; else `No OBD2 vehicle is connected.`
- Status: if the current status text ends with `…` (i.e. it still says `Asking the vehicle…`), replace it with the link text; otherwise leave it (it holds the error message, or is empty on first display).
- All three sections hidden, warning hidden, `Erase codes…` disabled.

When there **is a result** (counts: S stored, P pending, M permanent; `MilOn`; `ReportedCount` from PID 01; `Protocol`; `Trouble`):
- Headline:
  - clean (no codes in any list AND lamp off): `No fault codes. The warning lamp is off.`
  - lamp on: `The warning lamp is on. {Count}.`
  - lamp off with codes: `The lamp is off, but {Count lowercased}.`
  - where `{Count}` = parts joined with `, `: `{S} confirmed fault` (+`s` unless S = 1) if S > 0; `{P} pending` if P > 0; `{M} permanent` if M > 0; or `No codes were listed` if no parts (lamp on with nothing listed). Lowercasing applies to the whole string.
- Link: `{Protocol} · the vehicle counts {ReportedCount} confirmed`, or without a protocol name `The vehicle counts {ReportedCount} confirmed`.
- Warning (shown only when non-empty):
  - if `Trouble` non-empty: the trouble text, which is `The vehicle did not answer the request for {list} codes. What is listed may not be all of it.` with `{list}` = `stored` and/or `pending` joined by ` or ` (silence on the permanent mode never counts as trouble);
  - else if `ReportedCount > S`: `The vehicle says it has {ReportedCount} confirmed fault{s unless ReportedCount = 1} but listed {S}. A module that answers the count and not the list has not been fully read.`
  - else empty.
- Sections filled as above.
- `Erase codes…` enabled exactly when `S + P > 0` (permanent codes alone do not enable it).
- Status = summary: trouble text if any; else `No fault codes — lamp on.` / `No fault codes — lamp off.` when nothing listed; else parts `{S} stored`, `{P} pending`, `{M} permanent` (only non-zero ones) joined `, ` then ` — lamp on.` or ` — lamp off.`

#### Scan (read)

Trigger: `Scan` button, or the host rules below. Steps:
1. Status `Asking the vehicle…`; disable `Scan` and `Erase codes…`.
2. Ask the view model (`MainViewModel.ScanFaults()`; null when there is no OBD2 link). This is synchronous and holds the adapter: the live gauges pause for its duration (a second or two). Each of modes 03, 07, 0A is asked up to 3 times when silent; a refusal (`NO DATA`, `7F`, `UNABLE TO CONNECT`) is not retried; `NO DATA` means "none in this list". Then PID 01 gives lamp (top bit) and count (low 7 bits); if PID 01 fails, lamp off and count 0.
3. If it throws a protocol error, an I/O error or an invalid-operation error: result cleared, status = the exception's message.
4. Re-enable `Scan`; redraw.

**Open question:** the C# runs the scan on the UI thread, so the window freezes and `Asking the vehicle…` may never actually paint. The Rust may run it in the background, but must keep both buttons disabled until it finishes and must not let live polling interleave with the scan (the adapter is shared). Builder can settle.

#### Erase codes (CLEAR DTCs - a write to the vehicle)

Trigger: click on `Erase codes…` only. No shortcut, no menu item, no agent/MCP command exists for this. Enabled only after a scan that listed at least one stored or pending code (see rule above). If there is no current result the click does nothing.

1. Confirmation dialog, owner = the panel's window, title `OpenLogViewer`, warning icon, buttons **OK / Cancel**, default **Cancel**. Text (`{count}` = S + P from the last scan; `code` + `s` unless count = 1):

```
Erase {count} fault code{s} from the vehicle?

This clears more than the codes. The freeze frame — the record of what the engine was doing when the fault occurred — goes with them, and it cannot be recovered. So do the oxygen sensor test results and the readiness monitors, which the car has to re-earn over a full drive cycle before it can pass an emissions test.

It does not repair anything. A fault that is still present will set the code again.

Most vehicles refuse this with the engine running.
```

   Anything other than OK (Cancel, closing the dialog, Esc) -> nothing happens.
2. On OK: disable `Erase codes…`; status `Erasing…`.
3. Ask the view model to clear (`MainViewModel.ClearFaults()`): sends mode `04` once (no retry).
   - Throws protocol / I/O / invalid-operation error -> status = exception message, `Erase codes…` re-enabled, stop (no re-scan).
   - Returns null (no OBD2 link any more) -> status `No OBD2 vehicle is connected.`, stop (button stays disabled).
4. Result dialog: title `OpenLogViewer`, single **OK** button, icon Information if erased else Warning. Message:
   - not acknowledged, and the reply was a refusal: `The vehicle refused to erase its faults. Most will not do this with the engine running — try again with the ignition on and the engine off.`
   - not acknowledged, silence: `The vehicle did not answer the erase request, so nothing has been cleared.`
   - acknowledged, then mode 0A read back with no permanent codes: `Erased. The stored and pending codes, the freeze frame and the readiness monitors are all gone — the car will need a full drive cycle before it can pass an emissions test.`
   - acknowledged with N permanent codes remaining: `Erased, but {N} permanent code is still set. Only the controller can clear those, and it will not do so until it has watched the monitor pass.` (N = 1) / `Erased, but {N} permanent codes are still set. Only the controller can clear those, and it will not do so until it has watched the monitor pass.` (N > 1)
5. After the dialog is dismissed: a full re-scan (never assume the list is now empty).

**Open question:** the count in the confirmation comes from the last scan, which may be stale (e.g. scanned minutes ago, or a different car after a reconnect in the tab host - see below). Nothing checks the scan is current before erasing. User decision whether to require a fresh scan first.

#### Host 1: Calibration tab (OBD2 live)

- In Calibration mode, the panel fills all three columns of the calibration area and is visible exactly while `MainViewModel.IsObd2Live`. No Close button.
- The panel is attached at startup (draws the "no result" state; status empty).
- **Scan on first sight:** the first time the panel becomes visible while `IsObd2Live`, it scans once. Never again automatically for the rest of the run - coming back to the tab does not rescan.
- **Open question:** because "first sight" is once per application run, after a disconnect and a connection to a different OBD2 vehicle the tab still shows the previous vehicle's codes (and its enabled `Erase codes…` uses the old count) until the user presses `Scan`. Likely a bug; user decision (suggest: reset on each new OBD2 connection).
- The "Connect to an ECU to read its tune." notice is suppressed while OBD2 is live (`ShowNoTuneNotice = no ECU tune AND NOT IsObd2Live`) - owned by the calibration section.

#### Host 2: Fault codes window

- Opened from **Tools > `_Fault codes…`**, enabled when `IsObd2Live`, tooltip `Read and clear the vehicle's trouble codes — OBD2 only`. Also by command-line `--faults <out.png>` (opens, renders the window content to the PNG, closes, exits; needs a `--connect` before it to show anything).
- Window: title `Fault codes`, 720 x 620, min 520 x 420, centred on the main window, owned by it, not shown in the taskbar, background `Bg`. Its content is the panel with the `Close` button shown (closes the window). Esc does nothing special.
- Opening shows the window, then scans immediately.
- Single instance: if already open, choosing the menu item again restores it if minimised, brings it to the front and scans again. Closing it discards it (no stale list kept between openings).
- If the link drops while the window is open, the window stays; `Scan` then shows `Nothing was read.` / `No OBD2 vehicle is connected.`; `Erase codes…` (if still enabled from the old scan) leads through the confirmation to status `No OBD2 vehicle is connected.`

---

### Open questions

1. Dashboard insertion order after manual ticks mixes front-page and catalogue order, contrary to the comment "rebuilt in catalogue order". Spec ambiguity - builder can settle (match the stated rule).
2. A tune change re-seeds INI gauges, losing readings, peaks and lows, although `Retarget` exists to keep them. Builder can match; user decision if peaks should survive.
3. MaxxECU rev-counter rescale depends on the `Rev limit` gauge being on the dashboard (only dashboard gauges are fed). Builder can settle.
4. No on-canvas message when connected but no gauge is ticked. Builder can settle (C# shows none).
5. Fault scan and erase run synchronously on the UI thread in the C#. Builder can settle (background is fine if buttons stay disabled and polling does not interleave).
6. Calibration-tab fault panel scans only once per application run; after reconnecting to another OBD2 vehicle it shows the previous vehicle's codes and an `Erase codes…` count from them. Likely a bug - user decision.
7. Erase confirmation uses the last scan's count without checking it is current. User decision (safety).
8. Chosen dashboard is not persisted across restarts. Builder can match; user decision if it should be.

### Safety rules found

1. **Erase fault codes (OBD2 mode 04) is a write to the vehicle.** Trigger: `Erase codes…` button in the fault panel (Calibration tab or Fault codes window) only; enabled only when the last scan listed stored + pending > 0; requires OK on the confirmation dialog (title `OpenLogViewer`, OK/Cancel, default Cancel) whose text begins `Erase {count} fault code{s} from the vehicle?` (full text above). Sends `04` once; then reads mode 0A back and re-scans. Result messages quoted above.
2. Nothing else in this package writes to an ECU or vehicle: gauges are read-only displays; scanning (modes 03/07/0A and PID 01) only reads, but it pauses live polling while it holds the adapter.
3. No agent/MCP path can erase codes; the `--faults` command-line flag only scans and renders.


## §6 `w7-calculators`: Calculators and Estimate power windows

### Scope and summary

Two windows:

1. **Calculators** (`olv::calculators`): a stand-alone set of fifteen tuning calculators. It needs no log, no tune and no connection, and it **never writes anything to the tune, the ECU, the log or a file**. Every figure is worked out again as the user types. There is no Apply, Send or Copy button.
2. **Estimate power**: a small form that collects engine and injector facts, reports which power-estimation methods the open log can feed, and adds calculated (math) channels to the open log on request. It draws **no plot** and has no pull selection, vehicle mass, drag or gearing inputs. The added channels are plotted in the ordinary way through the channel list.

All arithmetic (`TuningMath`, `EngineRecipe`, `TurboSizing`, `DragStrip`, `Gearing`, `OctaneBlend`, `RunningCosts`, `ChargeAir`, `ChemicalIntercooling`, `ManifoldTuning`, `CamProfiles`, `EngineFamilies`, `EngineGeometry`, `PowerEstimate`) is ported in the core package. This section names the core call behind each output, plus the formatting and text the window adds around it. Strings that come from the core (verdicts, hints, notes, charts, warnings) are marked **(core)**.

Number formats are .NET format strings and use the current culture: `N0` = thousands separators, 0 decimals; `N2` = 2 decimals; `P0` = percent ×100 with 0 decimals; and so on.

---

### Opening the windows

| Entry point | Text | Enabled | Action |
|---|---|---|---|
| Main menu **Tools** | `Ca_lculators…` (Alt mnemonic L), tooltip `Boost, pressure ratio, injectors, fuel pump, lambda, octane, engine, gearing and airflow` | always | open or raise the Calculators window |
| Main toolbar, right-docked button | `ƒ  Calculators` (two spaces), padding 10,3, tooltip `Plan a build, size a turbo, injectors, fuel pump, octane, gearing — none of it needs a log open` | always | same |
| Main menu **Tools** | `Estimate _power…` (mnemonic P), tooltip `Calculated channels that estimate horsepower from the log` | only when a log document is open (`MainViewModel.HasDocument`) | open or raise the Power window |

**Single instance, kept alive.** Each window has one instance per main window.
- If none exists, the program creates one, owned by the main window, centred on it, and shows it. It is modeless and gets no taskbar entry.
- If one exists, the program restores it (when minimised) and brings it to the front. It is not rebuilt, so every typed value survives.
- Closing a window throws its instance away. The next open starts again from the defaults.
- The Calculators window does not otherwise refresh when it is raised. The Power window does not refresh either (see open questions).

**Scripted capture (command line)** (App start-up):
- `--calculators <tab> <path>`: needs two following arguments. The program opens the Calculators window, selects the calculator whose list name equals `<tab>` (case-insensitive, compared against the exact names in the table below), lays it out, saves the window content as an image to `<path>`, closes the window and exits.
  - If no name matches, it writes `no calculator called '{tab}'` to the run log (timestamped `HH:mm:ss  message` line) and **still captures** whatever page is showing. That is the first one, "Engine recipe".
- `--power <path>`: needs one following argument. The program opens the Power window (whether or not a log is open; the menu's enabled rule does not apply here), saves the image, closes the window and exits. Any log opened by earlier arguments is the one estimated against.

---

### Calculators window

#### Frame

- Title `Calculators`. Initial size 840 × 760, minimum 620 × 460. Background `Bg`.
- **Bottom strip**, full width: background `Panel`, top border 1 px `Line`, padding 12,7.
  - Right: `Close` button, 80 wide. It closes the window.
  - Left: the static text `Every figure updates as you type.` (11 pt, `Muted`, trimmed with an ellipsis).
- **Left column**, full height above the strip: background `Panel`, right border 1 px `Line`. It holds the navigation list, 150 wide, margin 6,8, with a transparent background and no horizontal scroll.
- **Page area**, the rest: margin 8. Exactly one page is visible at a time. Each page scrolls vertically (scroll bar only when needed) and its content has a margin of 14.

#### Navigation list

Grouped. Each group starts with a header: the category name, 10 pt, semibold, `Muted` at 75 % opacity, margin left 10, top 10, bottom 3.

| Row look | Rule |
|---|---|
| Normal | text `Muted`, transparent background |
| Hover | background `Panel` |
| Selected | background `Selected`, text `Text` |

Every row has padding 10,6, a 1 px vertical gap between rows, corner `CornerMedium`, and is trimmed with an ellipsis. Clicking a row (or moving with the arrow keys, the ordinary list behaviour) shows that page and hides all the others. On open the first row is selected.

Categories and calculators, in this exact order. The name column is what `--calculators` accepts:

| Category | Calculator name (list text) | Page |
|---|---|---|
| Plan a build | `Engine recipe` | Recipe |
| Air & boost | `Boost` | Boost |
| Air & boost | `Pressure ratio` | Compressor |
| Air & boost | `Turbo sizing` | Turbo |
| Air & boost | `Airflow` | Airflow |
| Air & boost | `Intercooling` | Intercooler |
| Fuel | `Injectors` | Injectors |
| Fuel | `Fuel pump` | Fuel pump |
| Fuel | `Lambda` | Lambda |
| Fuel | `Octane` | Octane |
| Engine | `Engine` | Engine |
| Engine | `Runners & headers` | Manifold |
| Drivetrain | `Gearing` | Gearing |
| Drivetrain | `Drag strip` | Drag strip |
| Running costs | `Fuel economy` | Running costs |

#### Shared page conventions

**Form layout.** Each form is a three-column grid:
- label column: fixed width, given per page;
- value column: auto width;
- note or unit column: takes the rest of the width.

Blank spacer rows 12 or 14 px tall separate inputs from outputs where the page uses them.

| Element | Look |
|---|---|
| Label | `Text`, right margin 10, vertically centred |
| Numeric field | text box 90 wide, right-aligned text, vertical margin 2 |
| Unit or note (right of the value) | 11 pt, `Muted`, left margin 8, vertically centred. Text is static or dynamic, as listed. |
| Answer (output) | `DataFont`, 15 pt, `Accent`, right-aligned in the value column. "Large" answers are 19 pt. |
| Note paragraph | 11 pt, `Muted`, wraps, top margin 10 |
| Heading | 11 pt, semibold, `Muted`, margin top 16, bottom 5. Written in capitals in the source text. |
| Panel box | background `Panel`, 1 px `Line` border, corner `CornerMedium`, padding 12,8 (or as stated). Boxes marked "left-hugging" are only as wide as their content. Inside: a title line (11 pt `Text`, bottom margin 6), then a monospaced body (`DataFont` 11 or 12, `Muted` or `Text`). |
| Combo box | 150 wide unless stated, left-aligned, vertical margin 2 |
| Intro line | some pages start with a line in the Label style, wrapped, bottom margin 10 |

**Field focus behaviour (every numeric field, including the narrow ones and the gear ratio box):**
- When a field gains keyboard focus by any means (Tab, click), its whole text is selected.
- The first mouse click on an unfocused field only focuses it, so the selection stays. A later click places the caret normally.
- There is no Enter or Esc handling, and nothing happens on focus loss. A value takes effect on every keystroke.

**Parsing.** A field is read as a floating-point number in the current culture (leading sign, decimal point and exponent allowed). Anything unparsable, including an empty box, reads as NaN. There is no error message and no red border: invalid input simply gives `—` in the outputs that depend on it. Integer inputs (cylinders) are the parsed number truncated toward zero; NaN truncates to 0.

**Output helpers.** Use these exact rules:
- `Show(v, d)`: `—` if v is NaN, infinite **or ≤ 0**; otherwise v formatted `N{d}`.
- `Round(v, d)`: `—` if NaN or infinite; otherwise v rounded to d decimals (banker's rounding, `Math.Round` default), printed in the shortest round-trip form with no thousands separator. For example 0.689, 24.7 and 12000.
- `Money(v, d)`: `—` if NaN, infinite or < 0; otherwise `$` + v formatted `N{d}`.
- `—` is U+2014.

**Programmatic writes.** When the window fills a field itself, for a conversion or a carried value, that write must not trigger the field's own "user typed" handler. Use a re-entrancy guard (the `_updating` flag). The guard is also held for the whole constructor.

**Window-wide barometric pressure** (`_barometricKpa`, initial 101.325):
- It is set only from the Pressure ratio tab: the Altitude or Barometric pressure field.
- If the value is NaN or ≤ 0 it falls back to 101.325.
- Every change re-derives the Boost tab's absolute pressure from its gauge kPa, then recalculates **every** page.

**Recalculate-all** runs at construction and on every barometric change, in this order: Boost, Pressure ratio, Turbo, Recipe, Injectors, Pump, Lambda, Engine, Gearing, Drag strip, Octane, Airflow, Running costs, Intercooling, Manifold. Otherwise each field change recalculates **only its own page** (see the open question on cross-tab staleness).

**Common combo contents:**
- **Fuel list**, ten entries in this order:
  1. `Petrol / gasoline`
  2. `E10`
  3. `E30`
  4. `E50`
  5. `E85`
  6. `Ethanol (E100)`
  7. `Methanol`
  8. `Diesel`
  9. `LPG / propane`
  10. `CNG / methane`

  Default: the first. The short names used inside notes are, in the same order: `Petrol`, `E10`, `E30`, `E50`, `E85`, `Ethanol`, `Methanol`, `Diesel`, `LPG`, `CNG`.
- **Engine family list ("Head and cams" / "Engine type")** (core `EngineFamilies.All`), six entries:

  | # | Name | VE % |
  |---|---|---|
  | 0 | `Older two-valve` | 75 |
  | 1 | `Modern two-valve` | 85 |
  | 2 | `Four-valve, fixed cams` | 92 |
  | 3 | `Four-valve with cam phasing` | 97 |
  | 4 | `Race, tuned intake` | 105 |
  | 5 | `Measured or known` | custom |

  Each entry has a core note.
- **Temperature unit combo**: 62 wide, margin 8,2,0,2, entries `°C` and `°F`, default `°C`.

**Warnings list** (Recipe and Manifold pages):
- Shown under a heading `WORTH KNOWING`. The heading is hidden when the list is empty.
- Each warning is one row with a bottom margin of 6:
  - **Left:** a badge 52 × 18, corner `CornerSmall`, margin 0,1,8,0, aligned to the top. Its text is the severity word: 10 pt, semibold, centred, colour `Bg`.
  - **Badge background by severity:** `stop` → theme `RampWarm`; `watch` → `Marker`; anything else → `Muted`.
  - **Right:** the warning text, 11 pt, `Muted`, wrapped.
- Severity and text come from the core.

---

#### Page: Engine recipe

Intro: `One build, worked through end to end. Say what the engine is and what you want of it, and every part of the list comes back sized on the same assumptions.`

Label column 170. Grid rows 0–12. Row 10 is empty, so there is a blank gap between Charge temperature and Injector duty limit.

| Row | Label | Control | Default | Note column |
|---|---|---|---|---|
| 0 | Displacement | field | `2.0` | dynamic: `litres — {L×CubicInchesPerLitre:N0} cubic inches` if L > 0, else `litres` |
| 1 | Cylinders | field | `4` | `one injector each` |
| 2 | Target power | field | `500` | dynamic: `hp at the crank — {recipe.SpecificOutput:N0} per litre`; if SpecificOutput is NaN, `hp at the crank` |
| 3 | Peak torque at | field | `3500` | `rpm — where you want full boost by` |
| 4 | Peak power at | field | `7000` | dynamic: `rpm — {recipe.MeanPistonSpeed:N1} m/s of piston, {PistonSpeedVerdict (core)}`; if NaN, `rpm` |
| 5 | Fuel | fuel combo | Petrol | dynamic: `stoichiometric {stoich:N2}:1, BSFC {recipe.Bsfc:N2}` |
| 6 | Lambda | field | `0.80` | dynamic: `at full throttle — {recipe.Afr:N2}:1 on {ShortName}`; if Afr is NaN, `at full throttle` |
| 7 | Head and cams | engine family combo | follows VE (starts at index for 97) | `EngineFamilies.For(VE).Note` (core) |
| 8 | Volumetric efficiency | field | `97` | `% — type your own and the list follows` |
| 9 | Charge temperature | field + temp unit combo | `45` (°C) | after the combo: `after the intercooler — {other scale:N0} °F/°C`; if NaN, `after the intercooler` |
| 11 | Injector duty limit | field | `85` | dynamic: `% — {(100/duty)−1:P0} more injector than the target needs` when 0 < duty ≤ 100, else `% at the target` |
| 12 | Pump headroom | field | `20` | dynamic: `% — {headroom/100:P0} more pump than the target burns` when headroom ≥ 0, else `% over what is burned` |

Charge temperature note detail:
- If the unit is °F, the note shows the Celsius figure: `{c:N0} °C`.
- If the unit is °C, it shows `{F(c):N0} °F`.

Behaviour:
- **Any field change** rebuilds the recipe page.
- **Head and cams changed by the user:**
  - If the chosen family is not `Measured or known`, the VE field is set to `Round(familyVE, 0)`.
  - The page is then rebuilt.
- **After every rebuild** the family combo is set to `EngineFamilies.IndexFor(VE)` without triggering that combo's handler. The list follows the box: a typed VE that matches no family selects `Measured or known`.
- **Temperature unit switched:**
  - If the scale actually changed and the field parses, the number is converted (C→F or F→C) and written as `Round(x,1)`.
  - The page is then rebuilt. The physical temperature never changes.
- **Fuel changed:** the page is rebuilt. Nothing is carried, because lambda does not depend on the fuel.

The recipe (`EngineRecipe.Build`, core) also takes these values from other tabs:
- the window's barometric pressure;
- inlet loss and charge loss (Pressure ratio tab, psi × 6.894757);
- base rail pressure (Fuel pump tab, psi).

**THE RECIPE** heading, then a full-width panel box (padding 12,10) holding monospaced text (`DataFont` 12, `Text`). The text is built like this:
- If `AirAtPeakPower` is NaN, the text is exactly `  a displacement, a power target and an engine speed to make it at`.
- Otherwise the lines are, in order. `{x,7:N1}` means right-aligned in 7 characters:
```
  Air            {AirAtPeakPower,7:N1} lb/min at the power peak
                 {AirAtPeakTorque,7:N1} lb/min at the torque peak
  Boost          {BoostKpa/6.894757,7:N1} psi   ({ManifoldKpa:N0} kPa absolute)
  Pressure ratio {PressureRatio,7:N2}   at the compressor

  Turbo          {T0.Label} — {T0.InducerMm:N0} mm inducer, {T0.RatedHorsepower:N0} hp rated
                 or {Tn.Label} — {Tn.InducerMm:N0} mm, {Tn.Headroom:P0} spare      (for turbos 2 and 3, if present)

  Injectors      {InjectorCcEach,7:N0} cc/min each   ({InjectorLbHrEach:N1} lb/hr)
  Fuel burned    {FuelLitresPerHour,7:N0} L/h at the target
  Pump           {PumpLitresPerHour,7:N0} L/h   {GallonsPerMinute(PumpLitresPerHour):N2} gal/min   at {RailUnderBoostPsi:N0} psi
                 {Pump0.Name}[ × {count}]                (only if a pump was found; " × n" only when count > 1)
```
  - If there are no turbos, the Turbo line is `  Turbo          nothing in the catalogue covers this`.

Then the `WORTH KNOWING` warnings list (`recipe.Warnings`, core).

Then three static notes:
1. `Every part of this list is sized on one mixture and one fuel consumption, so the pieces match each other. The margins are separate and named — duty on the injectors, headroom on the pump, flow to spare on the compressor — so being more careful is a matter of moving a margin rather than quietly biasing an assumption.`
2. `The torque speed is not decoration. Airflow at the power peak says whether a compressor is large enough; airflow at the torque peak says whether it is too large. A turbocharger with plenty left at 7,000 rpm can be sitting off the left of its map at 3,500 and never spool — which is why two builds wanting the same power can want different turbochargers.`
3. `A starting point, not a build sheet. Nothing here knows about your cylinder head, your cam, your gearbox or what the engine is bolted into, and every figure carries the fuel consumption that was assumed rather than measured. Take it to the individual calculators to argue with the parts one at a time.`

#### Page: Boost

Intro: `Type into any box; the others follow.` Label column 150. A 14 px spacer separates rows 2 and 4.

| Row | Label | Control | Default | Unit |
|---|---|---|---|---|
| 0 | Boost (gauge) | field `BoostPsi` | `10` | `psi` |
| 1 | Boost (gauge) | field `BoostBar` | `Round(10×6.894757/100, 3)` = `0.689` | `bar` |
| 2 | Boost (gauge) | field `BoostKpa` | `Round(68.94757, 1)` = `68.9` | `kPa` |
| 4 | Manifold pressure | field `MapKpa` | `Round(101.325+68.94757, 1)` = `170.3` | `kPa absolute — what an ECU reports` |
| 5 | Manifold pressure | field `MapPsi` | `Round(170.27257/6.894757, 2)` = `24.7` | `psi absolute` |
| 6 | Charge density | answer | — | `× sea-level air` |

The five fields are linked. Each conversion writes the other fields without re-triggering them (KpaPerPsi = 6.894757293168361, KpaPerBar = 100):
- **psi typed:** kPa = psi × 6.894757. Sets kPa `Round(kPa,1)` and bar `Round(kPa/100,3)`. Sets MAP kPa `Round(abs,1)` and MAP psi `Round(abs/6.894757,2)`, where abs = gauge kPa + barometric.
- **bar typed:** kPa = bar × 100. Sets kPa `Round(,1)` and psi `Round(kPa/6.894757,2)`, then the absolute fields as above.
- **kPa typed:** sets psi `Round(,2)` and bar `Round(,3)`, then the absolute fields.
- **MAP kPa typed:** sets MAP psi `Round(abs/6.894757,2)`. Sets gauge = abs − barometric into kPa `Round(,1)`, psi `Round(,2)` and bar `Round(,3)`.
- **MAP psi typed:** abs = psi × 6.894757. Sets MAP kPa `Round(abs,1)` and the gauge fields as above.

Output:
- **Charge density** = `ChargeDensityRatio(MapKpa field)` (core; against sea level on purpose), formatted `N2`, or `—` if NaN. Unlike `Show`, zero and negative values are printed.

Notes:
- Static: `A boost gauge reads zero at atmospheric; an ECU reports manifold pressure absolutely, where atmospheric is about 101 kPa. Ten psi of boost is 170 kPa absolute, not 69 — mixing the two up is worth a whole atmosphere.`
- Dynamic: `{BarometricSummary} Absolute pressure is gauge plus that, so the same boost is less absolute pressure — and less air — the higher you are.`

**BarometricSummary** (used on several pages):
- Text: `Barometric pressure is set to {baro:N1} kPa ({where}) on the Pressure ratio tab.`
- `where` is `sea level` when |altitude in feet| < 50; otherwise `{feet:N0} ft`.
- feet = `AltitudeMetres(baro)/0.3048`.

#### Page: Pressure ratio

No intro. Label column 170. A 14 px spacer separates inputs from outputs.

| Row | Label | Control | Default | Unit |
|---|---|---|---|---|
| 0 | Boost (gauge) | field | `12` | `psi at the manifold` |
| 1 | Altitude | field | `0` | dynamic: `feet — {metres:N0} m, {baro:N1} kPa`; if metres is NaN, `feet above sea level` |
| 2 | Barometric pressure | field | `Round(101.325,1)` = `101.3` | `kPa — your ECU's own reading beats the altitude` |
| 3 | Inlet loss | field | `Round(TypicalInletLossKpa/6.894757,1)` = `1` | `psi — air filter and the pipe to the turbo` |
| 4 | Charge loss | field | `2` | `psi — intercooler and charge pipe` |
| 6 | Compressor inlet | answer `Show(InletKpa/6.894757, 2)` | | `psi absolute — below ambient, past the filter` |
| 7 | Compressor outlet | answer `Show(OutletKpa/6.894757, 2)` | | `psi absolute — above the manifold, past the intercooler` |
| 8 | Pressure ratio | answer, 19 pt, `Show(Ratio, 2)` | | `what to read the compressor map at` |

Behaviour:
- **Altitude typed:**
  - baro = `BarometricKpa(feet × 0.3048)` (core).
  - Barometric field ← `Round(baro,1)`.
  - Apply the barometric value window-wide (see conventions), then recalculate all.
- **Barometric typed:**
  - Altitude field ← `Round(AltitudeMetres(kPa)/0.3048, 0)`.
  - Apply the barometric value window-wide, then recalculate all.
- **Boost, inlet loss or charge loss typed:** recalculates this page only.
- **Outputs:** come from `CompressorPressures(boost kPa, baro, inletLoss kPa, chargeLoss kPa)` (core).

Notes:
- Dynamic note:
  - `—` when the ratio is NaN, or when the bare ratio is NaN or ≤ 0.
  - Otherwise: `Boost over a sea-level atmosphere alone would read {bare:N2}. The filter, the intercooler and the air you are actually in put {(ratio/bare)−1:P0} on top of that, and it is the larger figure the compressor has to make.`
  - bare = `CompressorPressures(boost).Ratio`: sea level, no losses.
- Static note: `Outlet over inlet, both absolute and both taken at the compressor rather than at the manifold — which is why it is not simply boost over atmospheric. Both losses push it up, so a compressor picked without them is picked on a smaller number than it will be asked for.`

#### Page: Turbo sizing

Intro: `The sizing worked backwards: from the power you want to the air it takes, the boost that needs, and the turbochargers that could do it.` Label column 170.

| Row | Label | Control | Default | Unit / note |
|---|---|---|---|---|
| 0 | Target power | field | `650` | `hp at the crank` |
| 1 | Displacement | field | `5.7` | `litres — {L×CID:N0} cubic inches` if L > 0, else `litres` |
| 2 | Peak-power speed | field | `6000` | `rpm — where the target is made` |
| 3 | Head and cams | engine family combo | follows VE | family note (core) |
| 4 | Volumetric efficiency | field | `80` | `% at the power peak` |
| 5 | Fuel | fuel combo | Petrol | `stoichiometric {stoich:N2}:1 — the two boxes below follow it` |
| 6 | Air-fuel ratio | field | `11.5` | `at full throttle — lambda {LambdaFromAfr(afr,fuel):N2} on {ShortName}` if afr > 0, else `at full throttle` |
| 7 | BSFC | field | `0.46` (TurboSizing.RatedBsfc) | `lb/hp/hr — the ratings assume 0.46 on petrol` (`{RatedBsfc:N2}`) |
| 8 | Charge temperature | field + temp unit combo | `55` °C | `in the manifold — {other scale:N0} °F/°C`; if NaN, `in the manifold, after the intercooler` |
| 9 | Losses | field | `1` | `psi through the filter (intercooler: Pressure ratio tab)` |
| 11 | Air needed | answer 19 pt, `Show(AirLbPerMinute,1)` | | `lb/min — what a compressor map is read in` |
| 12 | Boost needed | answer: `(BoostKpa/6.894757):N1`, or `—` if NaN. Negative values are printed. | | `psi — {ManifoldKpa:N0} kPa absolute in the manifold`, or `psi` |
| 13 | Pressure ratio | answer `Show(PressureRatio,2)` | | `{inlet/6.894757:N1} psia in, {outlet/6.894757:N1} out, drawing {baro:N0} kPa`, or `at the compressor` |
| 14 | Headroom wanted | field | `10` | `% of flow spare, over what is needed` |

The calculation is `TurboSizing.Required(power, afr, bsfc, litres, rpm, VE, chargeC, baro, inletLoss kPa, chargeLoss kPa from the Pressure ratio tab)` (core).

Behaviour:
- **Fuel changed** (the mixture and the BSFC are carried, not replaced):
  1. lambda = `LambdaFromAfr(AFR field, previous fuel)`. If that is NaN or ≤ 0, use 0.78.
  2. petrolBsfc = `PetrolEquivalentBsfc(previous fuel, BSFC field)`. If that is NaN, use 0.46.
  3. AFR field ← `Round(AfrFromLambda(lambda, new fuel), 2)`.
  4. BSFC field ← `Round(SuggestedBsfc(new fuel, petrolBsfc), 3)`.
  5. Remember the new fuel as the "previous fuel" for next time.
- **Head and cams** and the **temperature unit** behave exactly as on the Recipe page: the family sets VE unless it is custom, the list follows VE after every recalculation, and the unit switch converts with `Round(,1)`.
- **Headroom:** a value of NaN or < 0 means 0.10.

Turbocharger suggestions (`TurboSizing.Suggest(air, headroom)`, core) appear in a left-hugging panel box:
- **Title:** `Turbochargers that could pass {air:N1} lb/min with {headroom:P0} spare` when air > 0, else `Turbochargers`.
- **Body** (`DataFont` 12, `Muted`): one line per match:
  `  {Label,-16} {InducerMm,3:N0} mm  {RatedHorsepower,5:N0} hp  {MaxFlowLbPerMinute×Count,5:N0} lb/min  {Headroom,5:P0} spare`
  (`,-16` means left-aligned, padded to 16).
- **Empty body:** `  nothing in the list, even in pairs` if air > 0, else `  a power target and an engine to put it on`.

Dynamic note under the box:
- **No picks and air > 0:** `Past what this short list covers, singly or in pairs. Garrett's own range goes a great deal further than the eleven frames here — this is a starting point rather than a catalogue.`
- **Picks exist and the first pick is a pair (Count > 1):** `No single turbocharger on the list does it, so these are pairs. Two smaller ones is a real answer to a large engine rather than a consolation prize: they spool sooner than the one big one that would be needed, and each sits nearer the middle of its own map.`
- **Otherwise:** `Ratings are the maker's own — on the G series the horsepower is the model number. The flow each one is credited with is worked out from that rating at 11.5:1 and a BSFC of 0.46, which is what the maker's own worked example uses, rather than transcribed off a compressor map — the same map gives a different maximum depending which island and which pressure ratio you read it at. Check the current catalogue before buying anything on the strength of this.` (11.5 is `{RatedAfr:N1}` and 0.46 is `{RatedBsfc:N2}`.)

Static notes:
1. `The smallest that can pass the air is listed first, because it is the one that will spool soonest and spend most of its life near the middle of its map rather than off the left edge of it. Anything below it in the list flows more and lags more.`
2. `This is not a compressor map. A turbocharger that flows enough may still be wrong — choked at low speed, past the edge of its island at the ratio wanted, or simply too large to spool on the engine. Flow and pressure ratio narrow the field to the ones worth looking at; the map decides between them, and there is no substitute for reading one.`

#### Page: Airflow

No intro. Label column 150.

| Row | Label | Control | Default | Unit / note |
|---|---|---|---|---|
| 0 | Displacement | field | `2.0` | `litres — {L×CID:N0} cubic inches`; if NaN, `litres` |
| 1 | Engine speed | field | `7000` | `rpm` |
| 2 | Volumetric efficiency | field | `95` | `% — 80 ordinary, 100 good, more at resonance` |
| 3 | Boost | field | `10` | `psi — {abs:N0} kPa absolute, {ratio:N2}× sea-level air`; if ratio is NaN, `psi` |
| 4 | Lambda | field | `0.85` | `at full throttle — {AfrFromLambda(λ,Petrol):N1}:1 on petrol`; if NaN or ≤ 0, `at full throttle, not at cruise` |
| 5 | BSFC on petrol | field | `0.5` | `lb/hp/hr — efficiency, not the sizing figure` |
| 7 | Air demanded | answer `Show(cfm,0)` | | `cfm` |
| 8 | Air demanded | answer `Show(lbMin,1)` | | `lb/min — the units a compressor map uses` |
| 9 | Air demanded | answer `Show(CubicMetresPerHour(cfm),0)` | | `m³/hour` |
| 11 | Power on petrol | answer `Show(hp,0)` | | fuel power note |
| 12 | Power on ethanol | answer | | fuel power note |
| 13 | Power on methanol | answer | | fuel power note |

How the outputs are worked out (core):
- abs = `AbsoluteFromGauge(boost×6.894757, baro)`.
- ratio = `ChargeDensityRatio(abs)`. This is against sea level on purpose.
- cfm = `CubicFeetPerMinute(L, rpm, VE, ratio)`.
- lbMin = `AirPoundsPerMinute(cfm)`.
- hp = `HorsepowerFromAir(lbMin, fuel, λ, bsfc)` for Petrol, Ethanol and Methanol.

**Fuel power note:**
- With afr = `AfrFromLambda(λ,fuel)` and own = `SuggestedBsfc(fuel,bsfc)`: if either is NaN, or hp ≤ 0, the note is `hp at the crank`.
- Otherwise it is `hp — {afr:N1}:1, BSFC {own:N2}`.
- For ethanol and methanol it also appends `, {(hp/petrolHp)−1:+0.0%;-0.0%} on petrol`. The custom format multiplies by 100 and puts an explicit + or ASCII −.

Notes:
- Dynamic note (AirPowerNote):
  - When petrol hp > 0: `The same air is very nearly the same power on any fuel: a pound of air carries about as much energy whichever fuel comes with it, so at the same lambda the alcohols are worth a few per cent and no more. What makes them worth having is that they resist knock and cool the charge, which buys boost and timing — and that arrives as more air, further up this tab, rather than as more power per pound of it.`
  - Otherwise: `Power needs a lambda and a BSFC above.`
- Static: `Demand, not delivery: this is what the engine would swallow at that speed and filling. What actually goes in is limited by whatever is upstream — the throttle, the intercooler, the filter and the compressor's own map.`
- Dynamic: `{BarometricSummary} The compressor's own pressure ratio moves the other way with altitude, and is on that tab too.`

#### Page: Intercooling

Intro: `How much heat there is to move, what a core will actually take out of it, and what spraying something into the charge does instead.`

All three grids have a 180 label column.

**THE CHARGE**

| Row | Label | Control | Default | Unit / note |
|---|---|---|---|---|
| 0 | Airflow | field | `40` | `lb/min — roughly {air×10:N0} hp worth of air` if air > 0, else `lb/min through the compressor` |
| 1 | Boost | field | `20` | `psi — a pressure ratio of {ratio:N2}` if boost > 0, else `psi of boost` |
| 2 | Ambient air | field | `85` | `°F drawn into the compressor` |
| 3 | Compressor efficiency | field | `70` | `% — 70 is ordinary, 78 is a good match` |
| 5 | Out of the compressor | answer 19 pt: `Round(hot,0) + " °F"` | | `{hot−ambient:N0} °F above ambient, all of which the core has to take back out`, or empty if hot is NaN |
| 6 | Heat to move | answer: `{heat:N0} BTU/min`, or `—` | | `{heat×60:N0} BTU/hr · {heat×60/3412.14:N1} kW, if it were all removed`, or empty |

How it is worked out (core): ratio = `ChargeAir.PressureRatio(boost psi)`; hot = `CompressorOutletF(ambient, ratio, eff/100)`; heat = `HeatLoadBtuPerMin(air, hot − ambient)`.

**THE CORE**

| Row | Label | Control | Default | Unit / note |
|---|---|---|---|---|
| 0 | Effectiveness | field | `70` | `% of the available drop the core takes — about 70 for a good air-to-air` |
| 1 | Core, W × H × thick | three narrow fields, 42 wide each, 3 px gap, right-aligned, same focus behaviour | `24`, `12`, `3` | `inches — {FrontalAreaSqIn:N0} in² face, {VolumeCuIn:N0} in³` if usable, else `inches, width × height × thickness` |
| 2 | Material | combo: `Aluminium`, `Copper / brass`, `Stainless steel` | Aluminium | `{ConductivityWmK:N0} W/m·K, {RelativeDensity:N1}× the weight of aluminium — the metal is {share:P2} of the resistance between the two air streams` |
| 4 | Charge after the core | answer 19 pt: `Round(out,0) + " °F"` | | `{out−ambient:N0} °F above ambient · {hot−out:N0} °F removed`, or empty |
| 5 | Density gained | answer: `{(density−1)×100:N1}%`, or `—` | | `more air at the same pressure, so about that much more torque before any timing the cooler charge now tolerates`, or empty |
| 6 | Core loading | answer: `{loading:N2}`, or `—` | | `BTU/min per in³ · {LoadingPerFrontalSqIn(heat×eff):N1} per in² of face`, or empty |

How it is worked out (core):
- out = `ChargeAir.OutletF(hot, ambient, eff/100)`.
- density = `DensityRatio(hot, out)`.
- core = `IntercoolerCore(W,H,T)`.
- loading = `core.LoadingPerCuIn(heat × eff)`.
- share = `CoreMaterials.MetalShareOfResistance(material)`.

Dynamic note: `{material.Note (core)}  There is no honest way to predict a core's effectiveness from its outside dimensions — that depends on the fin density, the internal geometry and how much air actually reaches it. What the loading figure is for is comparing two candidates: the same duty spread over half the core is twice the loading, and the smaller one will run hotter. Returns fall away sharply, too — a well-sized core doubled buys a few points of effectiveness, not another seventy.` There are two spaces after the material note.

**CHEMICAL INTERCOOLING — SPRAYING INTO THE CHARGE**

| Row | Label | Control | Default | Unit / note |
|---|---|---|---|---|
| 0 | Fluid | combo, 180 wide: `100% water`, `70/30 water-methanol`, `50/50 water-methanol`, `30/70 water-methanol`, `100% methanol`, `E85` | `50/50 water-methanol` (first name starting "50/50") | `{LatentBtuPerLb:N0} BTU/lb latent · {DensityGPerCc:N3} g/cc` |
| 1 | Drop wanted | field | `60` | `°F below the {out:N0} °F leaving the core`, or `°F to take out of the charge` if out is NaN |
| 2 | Evaporates in the charge | field | `75` (TypicalEvaporated × 100, `F0`) | `% — the rest wets the pipe or evaporates in the cylinder` |
| 3 | Fuel flow at this airflow | field | `4.0` | `lb/min, so the spray can be given as a share of it` |
| 5 | Nozzle needed | answer 19 pt: `{cc:N0} cc/min`, or `—` | | `at full flow · nozzles are sold in this, which is a volume — {fluid.Name} at {DensityGPerCc:N3} g/cc is what turns it into a mass`, or empty |
| 6 | As a mass flow | answer `{lb:N2} lb/min`, or `—` | | `{PercentOfFuel(lb,fuel):N0}% of fuel flow — controllers are usually set up in these terms, and 20 to 50 is the usual range`; empty if lb is NaN or fuel ≤ 0 |
| 7 | Consumption | answer `{GallonsPerHour(cc):N1} gal/hr`, or `—` | | `{LitresPerHour(cc):N1} l/hr · a 5 gallon tank lasts {TankMinutes(5,cc):N0} minutes at full flow`, or empty |
| 8 | Petrol it displaces | answer `{displaced:N2} lb/min`, or `—` (when lb is NaN) | | if displaced > 0: `{CombustibleFraction:P0} of this fluid burns — take that much petrol out, or it runs rich with the spray and lean the moment it stops`; else `water does not burn, so nothing here enriches — which is why a big water spray leans the mixture if the tune was relying on it` |
| 9 | Charge after the spray | answer `Round(out−drop,0) + " °F"` | | if after < ambient: `below the {ambient:N0} °F ambient — a spray can do that, where a core cannot`; else `{after−ambient:N0} °F above ambient`; empty if either is NaN |

The spray flows are `ChemicalIntercooling.FlowLbPerMin` and `FlowCcPerMin` with arguments (air, drop, fluid, out, 70, evap/100); 70 is a fixed constant (core). displaced = `PetrolDisplacedLbPerMin(lb, fluid)`.

Safety box: a full-width panel box, padding 12,9, holding text in `Marker` colour, 11 pt, wrapped: `An engine tuned to lean on a spray finds its detonation limit within seconds of the pump failing, and a nozzle that has quietly blocked gives no warning at all. Every system doing real work needs a flow sensor and a fallback that pulls boost or timing when the flow stops — sizing the nozzle is the easy half.`

Heading `ALL THE FLUIDS, SIDE BY SIDE`, then monospaced text (`DataFont` 11, `Text`). This is the table `FluidTable(air, drop, out, evap)`, trailing whitespace trimmed:
```
{"fluid",-24}{"BTU/lb",8}{"cc/min",10}{"lb/min",9}{"gal/hr",8}   burns
{Name,-24}{Latent,8:N0}{cc N0 or —,10}{lb N2 or —,9}{gal/hr N1 or —,8}   {CombustibleFraction,4:P0}     (one row per fluid, list order)
```

Static note: `Cooling figures assume the spray evaporates in the charge, where it buys density. Whatever evaporates later, in the cylinder, still works against detonation but does nothing for the number on this page. A spray is a way of going further than a core, not a way of making a small core behave like a large one — it stops the moment the tank is empty.`

**Open question:** `Round(NaN,0) + " °F"` shows `— °F` in the three temperature answers. Is that intended, or should it be a bare `—`? A builder can settle this; keep `— °F` for fidelity.

#### Page: Injectors

No intro. Label column 150.

| Row | Label | Control | Default | Unit / note |
|---|---|---|---|---|
| 0 | Target power | field | `400` | `hp at the crank` |
| 1 | Cylinders | field | `4` | `one injector each` |
| 2 | Fuel | fuel combo | Petrol | `stoichiometric {stoich:N2}:1` |
| 3 | BSFC | field | `0.6` (BoostedBsfc) | `TuningMath.BsfcHint(fuel)` (core) |
| 4 | Maximum duty | field | `85` | `% — above 85 many stop flowing linearly` |
| 6 | Each injector | answer `Show(cc,0)` | | `cc/min` |
| 7 | Each injector | answer `Show(lbHr,1)` | | `lb/hr` |
| 8 | All injectors | answer `Show(cc × max(cyl,0), 0)` | | `cc/min together` |

How it is worked out (core): lbHr = `InjectorPoundsPerHour(power, cyl, bsfc, duty)`; cc = `CcPerMinute(lbHr, fuel)`.

Note: `TuningMath.BsfcGuidance(fuel)` (core).

Changing the fuel recalculates only. Unlike the Pump and Turbo pages, the BSFC is **not** carried across.

**No "apply to tune" or "send to ECU".** The injector size is display only.

#### Page: Fuel pump

No intro. Label column 150.

| Row | Label | Control | Default | Unit / note |
|---|---|---|---|---|
| 0 | Target power | field | `400` | `hp at the crank` |
| 1 | Fuel | fuel combo | Petrol | (none) |
| 2 | BSFC | field | `0.6` | `BsfcHint(fuel)` (core) |
| 3 | Headroom | field | `20` | `% over what is burned` |
| 4 | Base rail pressure | field | `43.5` | `psi with no boost on it` |
| 5 | Boost | field | `0` | `psi — the pump works against {rail:0.#} psi` (rail = base + boost); if rail is NaN or ≤ 0, `psi — rail pressure rises with boost` |
| 7 | Fuel burned | answer `Show(burned,0)` | | `litres/hour` |
| 8 | Pump to look for | answer `Show(needed,0)` | | `litres/hour delivered at that pressure` |
| 9 | Same in | answer `Show(needed × UsGallonsPerLitre, 1)` | | `US gallons/hour — {GallonsPerMinute(needed):N2} a minute` if needed > 0, else `US gallons/hour` |

How it is worked out (core): burned = `FuelLitresPerHour(power, bsfc, fuel)`; needed = `PumpLitresPerHour(power, bsfc, fuel, headroom)`.

**Fuel changed** (the BSFC is carried by energy content):
1. petrol = `PetrolEquivalentBsfc(previous fuel, BSFC field)`. If that is NaN (diesel), use 0.60.
2. moved = `SuggestedBsfc(new fuel, petrol)`. If it is not NaN, BSFC field ← `Round(moved,2)`.
3. Remember the new fuel.
4. Recalculate.

Suggested pumps (left-hugging panel box):
- picks = `SuggestPumps(needed, rail, NeedsAlcoholSafePump(fuel))` (core).
- **Title:**
  - `Suggested pumps` when the rail is NaN or ≤ 0, or needed is not > 0.
  - Otherwise `Suggested pumps — {needed:N0} L/h at {rail:0.#} psi`.
  - When the fuel needs an alcohol-safe pump, it appends `, alcohol-rated only`.
- **Body** (`DataFont` 12, `Muted`): the first five picks, one line each:
  `  {Name,-20} {Nickname,-11} {"1 pump " or "{n} pumps",-8} {DeliveredLitresPerHour,5:N0} L/h[  E85]`
  `  E85` is appended when the pump is alcohol-safe.
- **No picks:** the body is `  nothing in the list gets there` when needed > 0 and rail > 0, else `  —`.

Note under the box (PumpPicksNote):
- **No picks, needed > 0 and rail > 0:** `Past about {MostPumpsWorthWiring} in-tank pumps the plumbing is doing more work than the pumps are: the shared line, filter and regulator become the restriction, and a surge tank stops being optional. This is where a belt-driven mechanical pump, or a brushless pump and controller, is the answer instead of another Walbro. Widening the search: a lower base rail pressure, or larger injectors so the rail is not asked for the flow at pressure, both move this.`
- **No picks otherwise:** `Enter a power, a fuel and a rail pressure.`
- **Picks:** an optional prefix, then fixed text.
  - The prefix appears only if the first pick's count > 1: `Nothing on the list does it alone, so the smallest answer is {count} in parallel. Each pump after the first is counted at {ParallelPumpEfficiency:P0} of its own flow, because they share a line, a filter and a regulator. Two pumps want a surge tank and a check valve on each, or the idle one becomes a leak path for the working one. `
  - The fixed text follows: `Flow shown is what the pump still makes at your pressure, estimated from its rating — roughly {PumpPressureFalloff:P0} lost for a doubling of pressure. Check the maker's own curve at your pressure and at the voltage your car actually holds, since these are quoted at 13.5 V and a sagging system is a slower pump. Part numbers and ratings are transcribed from published data and get superseded — verify before buying.`

Second left-hugging panel box:
- Title `Typical BSFC, lb per hp per hour`.
- Body `BsfcLegend(fuel)` (core, `DataFont` 12, `Muted`).

Then the note: `The BSFC follows the fuel, scaled by how much energy it carries per kilogram — so a figure you measured or chose for an aspirated engine survives the change rather than being replaced. Type over it whenever you know better.`

Static note: `A pump's headline figure is quoted at some pressure, usually low and often at 13.5 volts. Flow falls as rail pressure rises and as voltage sags, so compare against the maker's curve at your own pressure rather than against the number on the box.`

#### Page: Lambda

No intro. Label column 150.

| Row | Label | Control | Default | Unit / note |
|---|---|---|---|---|
| 0 | Fuel | fuel combo | Petrol | `stoichiometric at {stoich:N2}:1` |
| 1 | Lambda | field | `0.85` | `1.00 is stoichiometric on any fuel` |
| 2 | Air-fuel ratio | field | `Round(AfrFromLambda(0.85, Petrol), 2)` (stoich 14.7) | `on {Name(fuel)}`, the long fuel name |
| 4 | Reads as | verdict text spanning columns 1–2, `Accent`, wrapped | | |

Behaviour:
- **Lambda typed:** AFR ← `Round(AfrFromLambda(λ, fuel), 2)`.
- **AFR typed:** lambda ← `Round(LambdaFromAfr(afr, fuel), 3)`.
- **Fuel changed:** lambda stays put; AFR ← `Round(AfrFromLambda(λ, new fuel), 2)`.

The verdict is chosen from the lambda field. Test the rows in this order; NaN or ≤ 0 is checked first:

| Lambda | Verdict |
|---|---|
| NaN or ≤ 0 | `—` |
| < 0.70 | `richer than an engine will take — bore wash and misfire, not power` |
| < 0.80 | `very rich — safe under load, and down on power` |
| < 0.95 | `rich of stoichiometric — where an engine is run under load` |
| ≤ 1.02 | `about stoichiometric — cruise and idle` |
| ≤ 1.10 | `lean of stoichiometric — economy, not power` |
| otherwise | `very lean — fine on light throttle, dangerous under load` |

Static note: `Lambda is the same number on every fuel, which is why it is worth using. 12.5:1 is comfortably rich on petrol and lean enough to hurt on E85 — the same mixture is 0.85 on both.`

#### Page: Octane

No intro. Label column 150.

| Row | Label | Control | Default | Unit / note |
|---|---|---|---|---|
| 0 | Base petrol | field | `91` | `(R+M)/2 — the number on a US pump` |
| 1 | Sensitivity | field | `8` | `RON − MON — pump petrol is 7 to 10` |
| 2 | Blend with | combo: `Ethanol (E100)`, `Methanol`, `E85` | Ethanol | E85 → `itself 85% ethanol, so it dilutes as it blends`; Methanol → `more octane per litre than ethanol, and far colder`; otherwise → `RON 109, MON 90 neat` |
| 3 | Blended in | field | `30` | `% — {EthanolByVolume:P0} alcohol, {AlcoholMoleFraction:P0} by molecule` if AKI > 0, else `% by volume` |
| 5 | Anti-knock index | answer 19 pt `Show(AKI,1)` | | `(R+M)/2 — compare against the pump` |
| 6 | RON / MON | answer `{Ron:N1} / {Mon:N1}` if Ron > 0, else `—` | | `sensitivity {Ron−Mon:N1}, against the base's {sens:N1}` if Ron > 0, else `research and motor octane` |
| 7 | Heat of vaporisation | answer `Show(Hov,0)` | | `kJ/kg — {Hov/PetrolHov:N2}× neat petrol` if > 0, else `kJ/kg` |
| 8 | Charge cooling | answer `Show(Cooling,0)` | | `kJ per kg of air — {Cooling/PetrolCooling:N2}× petrol` if > 0, else `kJ per kg of air` |

The calculation is `OctaneBlend.Blend(base, sensitivity, stock, percent/100)` (core).

Left-hugging panel box with two charts:
- Title `Anti-knock index with {Name(stock)} by volume`, body `OctaneBlend.Chart(stock)` (core, `DataFont` 11, `Muted`).
- Second title (top margin 12) `Heat of vaporisation, and what it is worth per kg of air`, body `OctaneBlend.CoolingChart(stock)` (core).

Static notes:
1. `Octane blends very nearly linearly by molecule rather than by volume. Ethanol's molar mass is 46 against petrol's 105 and it is denser, so a tenth of the volume is a fifth of the molecules — which is the whole of why the first splash is worth so much more than the last. Estimated on that basis (Anderson et al., SAE 2012-01-1274), and checked against measured blends: on an 88 RON blendstock this gives 92.4 at E10 and 98.7 at E30, where the measurements are 92.4 and 98.6.`
2. `Two cautions. A pump number is the average of RON and MON, and blending alcohol in raises RON much harder than MON — so the index understates what a boosted or direct-injected engine actually gets, since those run nearer the RON end. And the cooling below is what the fuel can absorb, not what the charge keeps: a port-injected engine gives much of it back to the manifold walls, where a direct-injected one collects nearly all of it.`

Dynamic-constant note: `The chart is unaffected by the sensitivity above. Splitting a pump number into RON and MON adds half the sensitivity to one and takes it off the other, and averaging them back cancels it exactly — so the index a blend lands on does not depend on that assumption at all. It only changes the RON and MON shown separately. Petrol's own heat of vaporisation is a range rather than a figure — {PetrolHov:N0} kJ/kg is used here and published values run from about 305 to 350 — so the cooling figures are worth a tenth either way. The alcohols are compounds and are pinned much better than that.`

#### Page: Engine

Intro: `Bore and stroke decide all three of these, so they are asked for once.` Label column 160. Spacers of 14 px sit after rows 3, 7 and 12.

| Row | Label | Control | Default | Unit / note |
|---|---|---|---|---|
| 0 | Bore | field | `86` | `mm — {bore/25.4:N3} in` if > 0, else `mm` |
| 1 | Stroke | field | `86` | same pattern |
| 2 | Cylinders | field | `4` | `bore/stroke {shape:N2} — oversquare` (> 1.02), `undersquare` (< 0.98) or `square`; if NaN, `one piston each` |
| 3 | Engine speed | field | `7000` | `rpm — the limit you actually use` |
| 5 | Displacement | answer 19 pt: `(totalCc/1000):N2` if totalCc > 0, else `—` | | `litres — {totalCc:N0} cc, {CubicInches(totalCc):N0} cubic inches`, or `litres` |
| 6 | One cylinder | answer `Show(sweptCc,1)` | | `cc swept` |
| 7 | Mean piston speed | answer `Show(mps,1)` | | `m/s — {ft/min:N0} ft/min, {PistonSpeedVerdict(mps)}` (core), or `m/s` |
| 9 | Chamber volume | field | `42` | `cc in the head` |
| 10 | Gasket | field | `1.0` | `mm thick, compressed — {bore+1:N0} mm bore, {CylinderVolumeCc(bore+1, t):N1} cc` if t > 0 and bore > 0, else `mm thick, compressed` |
| 11 | Deck clearance | field | `0.5` | `mm below the deck at TDC — negative if it pokes out` |
| 12 | Piston crown | field | `5` | `cc — a dish is positive, a dome negative` |
| 14 | Compression ratio | answer 19 pt `{ratio:N2}:1`, or `—` | | `static — {clearance:N1} cc left above the piston`, or `static` |
| 15 | With your boost | answer `{boosted:N1}:1`, or `—` | | `an index at {map:N0} kPa from the Boost tab — not a real ratio`, or `an index, not a ratio` |

How it is worked out (core `EngineGeometry`):
- The gasket bore is always the bore + 1 mm.
- clearance = `ClearanceVolumeCc(bore, chamber, bore+1, gasket, deck, piston)`.
- ratio = `CompressionRatio(sweptCc, clearance)`.
- boosted = `BoostedCompressionIndex(ratio, Boost tab MapKpa field, baro)`.

Static notes:
1. `Mean piston speed says more about what an engine is being asked to survive than its rpm does. Seven thousand is an easy afternoon for a short stroke and the end of the road for a long one — the rings and the rod feel the distance, not the number on the tachometer. And it is a mean: the piston stops dead twice a revolution and passes through about 1.6 times this in the middle.`
2. `The compression ratio here is the static one, which is geometry and nothing else. A long-duration cam holds the inlet open past bottom dead centre and pushes some of the charge back out, so the engine behaves like less than this down low and comes back to it as the ports start to fill.`
3. Constant (set in code): `The boosted figure is the static ratio multiplied by the manifold pressure over the atmosphere the engine is breathing, and it is an index for comparing one combination against another rather than a compression ratio. It ignores the charge temperature boost brings with it, which moves the knock limit the wrong way, and the cam timing the static ratio ignores too. Its use is the trade it makes visible: less compression buys boost, and it says roughly how much.`

#### Page: Runners & headers

Intro: `Runner and primary lengths, port sizes, plenum and manifold volume — sized by the pressure waves running up and down the pipes. Length decides which engine speed the resonance helps at, so it is a choice about what the engine is for rather than one right answer.`

All grids have a 180 label column. All combos are 195 wide.

**THE ENGINE**

| Row | Label | Control | Default | Unit / note |
|---|---|---|---|---|
| 0 | Displacement | field | `2.0` | `litres — {L×1000/cyl:N0} cc a cylinder` (cyl > 0), else `litres` |
| 1 | Cylinders | field | `4` | `one runner and one primary each` |
| 2 | Induction | combo: `Naturally aspirated`, `Turbocharged` | Naturally aspirated | Turbo → `the turbine reflects nothing — volume decides`; NA → `the collector reflects — length is a real choice` |
| 3 | Build for | combo: `Quick spool`, `Street and strip`, `High-rpm race` | Street and strip | QuickSpool → `tuned at the torque peak, small fast ports — {TargetIntakeVelocity:N0} m/s`; HighRpmRace → `tuned at the power peak, big ports — {v:N0} m/s`; Balanced → `tuned between the two peaks — {v:N0} m/s` |
| 4 | Peak torque at | field | `4500` | Turbo → `rpm — where you want full boost by`; NA → `rpm — where the engine is asked to pull` |
| 5 | Peak power at | field | `7000` | `rpm — the ports are sized to flow here` |
| 6 | Engine type | engine family combo | index 3 `Four-valve with cam phasing` | head note (core) |
| 7 | Volumetric efficiency | field | `Round(CamProfiles.VolumetricEfficiency(family 3, Performance cam), 0)` | `% at peak power — your own figure` if the head or the cam is custom, else `% — the head and the cam together` |
| 8 | Compression ratio | field | `10.5` | `:1 — the volume the runner springs against` |

**THE CAM AND THE GAS**

| Row | Label | Control | Default | Unit / note |
|---|---|---|---|---|
| 0 | Cam | combo: `Stock`, `Stock +`, `Performance`, `Performance +`, `Full race`, `From your cam card` | follows the durations (starts at `Performance`) | custom → `your own figures, seat to seat`; else `CamProfiles.OverlapVerdict(overlap)` (core) |
| 1 | Inlet duration | field | `274` | `° crank the inlet valve is open, seat to seat` |
| 2 | Exhaust duration | field | `286` | `° crank — {WaveWindowDeg:N0}° from valve opening to overlap`, or `° crank — must exceed 180` |
| 3 | Charge temperature | field | `35` | `°C in the runner — sound runs at {c:N0} m/s`, or `°C in the runner` |
| 4 | Primary gas temperature | field | `600` | `°C mean along the pipe — sound at {c:N0} m/s`, or `°C mean along the primary, not at the valve` |
| 5 | Manifold pressure | field | `101.3` | `kPa absolute — {gauge psi:N1} psi gauge`, or `kPa absolute` |
| 6 | Exhaust back pressure | field | `101.3` | `kPa absolute — {gauge psi:N1} psi gauge`, or `kPa absolute in the manifold` |

The gauge psi in those two notes is worked out as follows:
- gauge psi = `Round(GaugeFromAbsolute(kPa, 101.325)/6.894757, 1) + 0.0`.
- The `+ 0.0` turns −0 into 0, so 101.325 shows `0.0`, not `-0.0`.
- These two notes always use the sea-level atmosphere, not the window barometric.

The cam presets come from core `CamProfiles.All`. Each row gives intake/exhaust seat-to-seat, @0.050 intake/exhaust, LSA, charge °C and exhaust °C:

| Cam | Intake/exhaust | @0.050 | LSA | Charge °C | Exhaust °C |
|---|---|---|---|---|---|
| Stock | 252/262 | 204/214 | 112 | 45 | 560 |
| Stock + | 262/270 | 218/224 | 110 | 40 | 580 |
| Performance | 274/286 | 230/236 | 110 | 35 | 600 |
| Performance + | 288/300 | 242/248 | 108 | 32 | 620 |
| Full race | 310/318 | 262/268 | 106 | 28 | 650 |
| From your cam card | custom | | | | |

Behaviour:
- **Induction changed:**
  - Turbocharged: Manifold pressure ← `Round(200,1)` = `200`; Back pressure ← `Round(240,1)` = `240`.
  - NA: both ← `101.3`.
  - Then recalculate.
- **Build for changed:** recalculate.
- **Cam changed to a preset** (not `From your cam card`):
  1. Fill Inlet duration, Exhaust duration, Charge temperature and Primary gas temperature with `Round(x,0)` of the preset.
  2. If the Engine type is not `Measured or known`, set VE ← `Round(CamProfiles.VolumetricEfficiency(family, cam), 0)` (unless it is NaN).
  3. Recalculate.
  - Choosing `From your cam card` only recalculates.
- **Engine type changed:** set VE from (family, `CamProfiles.For(inlet, exhaust)`) under the same rule, then recalculate.
- **After every recalculation:**
  - The Cam combo is set to `CamProfiles.IndexFor(inlet, exhaust)`.
  - Then the Engine type combo is set to `CamProfiles.FamilyIndexFor(VE, chosenCam, current index)`.
  - Neither of these re-triggers a handler. The lists follow the boxes, and on a tie the current head selection is kept.

Cam detail note (ManCamDetail):
- Custom cam: `Working from your own cam. Both duration boxes want the seat-to-seat figure; if what you have is duration at 0.050 in, it is roughly 45° shorter than the number this page wants.`
- Preset cam: `{Name} — {In:N0}/{Ex:N0}° seat to seat, {In50:N0}/{Ex50:N0}° at 0.050 in, {LSA:N0}° lobe separation, {Overlap:N0}° of overlap. {Note with first letter upper-cased}. Compare the 0.050 in pair against a cam card to see whether your grind is near this one.`

Static note: `Durations are seat to seat — the advertised figure. A cam card leads with duration at 0.050 in, which runs about 45° shorter and would take a fifth off every length on this page. Picking a cam above fills both boxes correctly; the list moves to your own figures the moment you type in one.`

**INTAKE** (from core `ManifoldTuning.Plan(spec).Intake`)

| Row | Label | Output | Note |
|---|---|---|---|
| 0 | Runner length | 19 pt `Show(Recommended.LengthMm,0)` | if order > 0: `mm from valve to plenum, at order {order}, tuned for {TunedRpm:N0} rpm`, plus ` — does not package` if not practical; else `mm from valve to plenum` |
| 1 | Runner diameter | 19 pt `Show(dia,1)` | `mm bore — {area:N0} mm² of port`, or `mm bore` |
| 2 | Runner volume | `Show(vol,0)` | `cc each, {vol×cyl:N0} cc in all the runners`, or `cc each` |
| 3 | Port velocity | `Show(vPower,0)` | `m/s at peak power, {vTorque:N0} at torque — {VelocityVerdict}` (core), or `m/s mean at peak power` |
| 4 | Plenum volume | 19 pt `Show(plenum,0)` | `cc at {multiple:N2}× — {PlenumVerdict(multiple, induction)}` (core), or `cc` |
| 5 | Plenum, if you'd rather | **input** field, default empty | `× displacement — blank takes the figure the goal wants` |
| 6 | Plenum and runners | `Show(tract,0)` | Turbo → `cc to pressurise — but the intercooler and pipes are ~3× this again`; NA → `cc of plenum and runners together` |
| 7 | Resonance | `Show(Hz,0)` | `Hz — ratio {HelmholtzRatio:N1} to the tuned speed (3 to 4 is tuned)`, or `Hz, runner against cylinder` |

An empty or unparsable plenum multiple is sent to the core as 0, which means "unset".

**Plenum table** (full-width panel box, padding 10, margin 0,6; monospaced `DataFont` 12, `Text`):
```
{"plenum",-9}{"volume",11}{"+ runners",13}   what it does
--------------------------------------------------------------------------   (74 dashes)
{m,5:N2}×{" *" if in use else "  ",-3}{"{vol:N0} cc",11}{"{vol+runners:N0} cc",13}   {PlenumVerdict(m, induction)}
...
(blank line)
* in use[   — and the intercooler and its pipework are around three times the whole column again]   (suffix only when turbocharged)
```
- Rows are `ManifoldTuning.PlenumMultiples`, plus the multiple in force if it is > 0 and more than 0.005 from every step. The rows are sorted ascending.
- A row is "in use" when it is within 0.005 of the multiple in force.
- Rows whose volume is NaN are skipped.
- runners = runner volume × max(cyl, 0).

Heading `EVERY LENGTH THAT WOULD WORK`, then a panel box (padding 10, bottom margin 6) holding the **order table**:
```
{"",-8}{"intake runner",20}{"exhaust primary",20}
{"order",-8}{"mm",20}{"mm",20}
------------------------------------------------   (48 dashes)
{order,-8}{cell,20}{cell,20}      one row per order, as many as the longer list
(blank line)
* the one to build   · packages   (no mark) too long or too short
```
- A cell is `{LengthMm,7:N0} {mark}`: mark `*` for the recommended order, `·` if practical, a space otherwise.
- A missing order or a NaN length shows `—`.
- On a turbocharged engine the exhaust column never shows `*`.
- The order number is taken from the intake entry, else the exhaust entry, else row index + 1.

Static note: `A lower order is a longer pipe and a stronger pulse, so the recommendation is the longest one that still fits in a car. If it will not package, going up an order is the usual answer and costs some of the effect rather than all of it.`

**EXHAUST**

| Row | Label | Output | Note |
|---|---|---|---|
| 0 | Primary length | 19 pt `Show(len,0)` | Turbo → `mm if tuned — but build it short and equal instead`; NA with order > 0 → `mm from valve to collector, at order {order}, tuned for {TunedRpm:N0} rpm`; else `mm from valve to collector` |
| 1 | Primary diameter | 19 pt `Show(dia,1)` | `mm bore — {dia/25.4:N2} in, nearest tube {tube}`, or `mm bore` |
| 2 | Primary volume | `Show(vol,0)` | `cc in one primary` |
| 3 | Gas velocity | `Show(v,0)` | `m/s mean at peak power — {ExhaustVelocityVerdict}` (core), or `m/s mean at peak power` |
| 4 | Collector diameter | `Show(dia,1)` | `mm — twice a primary's area, which is 1.41 times its bore`, or `mm` |
| 5 | Collector length | `Show(len,0)` | `mm — about half a primary` |
| 6 | Manifold volume | `Show(total,0)` | Turbo → `cc to fill before the turbine sees pressure — this is spool`; NA → `cc of primaries, all cylinders together` |

**Nearest tube:**
- `—` if the diameter is NaN or ≤ 0.
- Otherwise round inches × 8 to a whole number and divide by 8. Format `N3` + ` in`, then remove the text `.000`. For example `1.750 in`, `2 in`.

After that:
- The `WORTH KNOWING` warnings list (`plan.Warnings`, core).
- Static note: `Lengths are measured from the valve head to the open end — the port in the cylinder head is part of the runner and part of the primary, and forgetting it is the commonest way one of these comes out long. The figure given is what a tape measure should read; the acoustic length it came from is a few millimetres more, because a pipe behaves longer than it is.`
- Dynamic note (ManBellNote):
  - When turbocharged, or when Bell's length is NaN: `The exhaust length is worked out from the speed of sound in the gas and the time the valve is open — no fitted constants. On a turbocharged engine it is shown for interest: the turbine is what the pipe actually has to feed.`
  - Otherwise: `Cross-check: A. Graham Bell's published formula gives {BellLengthMm:N0} mm for this cam and speed, against the {Recommended.LengthMm:N0} mm derived here from the speed of sound and the valve timing. The two share no constant, and across the usual range of cams and engine speeds they agree within a few per cent.`
- Static note: `This sizes pipework from wave timing and mean velocity. It knows nothing about port shape, valve curtain area, the short-side radius or a badly cast bend, and no length here will rescue a port that does not flow. It gets a design to the right size before any of that is worth measuring.`

#### Page: Gearing

No intro. Label column 150.

| Row | Label | Control | Default | Unit / note |
|---|---|---|---|---|
| 0 | Tyre | field (text) | `245/40R18` | if parsed by `Gearing.TryParseTyre`: `{tyre} — {DiameterMm:N0} mm, {DiameterInches:N1} in across`; else `as written on the sidewall — 245/40R18` |
| 1 | Tyre squat | field | `3` | `% squat — {circ:N0} mm of road per turn, {MmPerMile/circ:N0} turns per mile`, or `% the loaded tyre squats` |
| 2 | Final drive | field | `3.90` | `differential ratio` |
| 3 | Redline | field | `7000` | `rpm — where you actually shift` |
| 4 | Cruising at | field | `70` | `mph — engine speed for it, per gear` |
| 5 | Gear ratios | wide text box spanning columns 1–2, stretching, same focus behaviour | `3.545, 2.048, 1.416, 1.059, 0.848, 0.756` | — |
| 7 | Top speed | answer 19 pt `Show(top.Mph,0)` | | `mph — {top.Kph:N0} km/h, {redline:N0} rpm in {ordinal}` |
| 8 | Engine speed | answer `Show(top.RpmAtCruise,0)`, `—` if NaN | | `rpm at {cruise:N0} mph in {ordinal}`, or `rpm` |

**Gear ratio parsing:**
- Split the text on comma, space, tab, semicolon and slash, dropping empty pieces.
- Parse each piece in the current culture. Unparsable pieces and values ≤ 0 are skipped; they never become a zero.
- **Open question:** in a culture whose decimal separator is a comma, `3,545` is split into two gears. A builder can settle this; the source has the same behaviour.

How it is worked out (core): circ = `RollingCircumferenceMm(diameter, squat)`; table = `Gearing.Table(ratios, final, redline, circ, cruise)`.

Ordinal: 1 → `1st`, 2 → `2nd`, 3 → `3rd`, anything else → `{n}th`.

Left-hugging panel box:
- **Title:** `{n} gears at {redline:N0} rpm` when n > 0, else `Gears`.
- **Body:** `Gearing.Chart(table, cruise > 0)` (core, `DataFont` 11, `Muted`), or `  a tyre, a final drive, a redline and some ratios` when the table is empty.

With an empty table: Top speed and Engine speed show `—`, their notes show `mph` and `rpm`, and GearNote is empty.

Static notes:
1. `Columns are the speed at the redline in that gear, the same in km/h, how many mph the gear is worth per thousand rpm, and where the engine lands on taking the next one. That last is the number that decides a gearbox: a wide step drops the engine out of the power band, and a car that pulls hard to the redline in third and falls flat in fourth is telling you about the step rather than about the tune.`
2. `A rolling tyre covers about three per cent less ground than pi times its diameter, because it squats under the car. Working from the geometry alone reads that much fast — which is roughly what a factory speedometer already does, and why the two errors are easy to mistake for each other. Set the squat to zero to get the figure the usual rpm × diameter ÷ 336 rule gives.`

GearNote, shown when the table is not empty: `This is the geared top speed: the tallest gear at the redline, and nothing else. It assumes the engine can still pull that gear against the air, which past about a hundred and fifty miles an hour is a large assumption. A car geared taller than its power can push never reaches the redline in top at all, and what it actually does is set by drag — so treat this as the ceiling the gearbox imposes rather than as a speed the car will see.`

#### Page: Drag strip

Intro: `What a power figure is worth at the strip — and, from a timeslip, what the run says about the car.` Label column 170.

| Row | Label | Control | Default | Unit / note |
|---|---|---|---|---|
| 0 | Weight | field | `3200` | `lb with the driver in it — {w×0.45359237:N0} kg` if > 0, else `lb, with the driver in it` |
| 1 | Power | field | `400` | `hp at the crank — {w/p:N1} lb per horsepower` if both > 0, else `hp at the crank` |
| 2 | Correlation | combo: `Hale`, `Fox`, `Huntington` | `Fox` | the formula's note (core). Hale → `a run that hooked up — sticky tyres and a launch that worked`; Fox → `between the two, and the usual middle answer`; Huntington → `the oldest and most forgiving — where a street car on street tyres lands` |
| 4 | Quarter mile | answer 19 pt `Show(QuarterEt,2)` | | `seconds at {QuarterMph:N1} mph`, or `seconds` |
| 5 | Eighth mile | answer `Show(EighthEt,2)` | | `seconds at {EighthMph:N1} mph`, or `seconds` (both test whether **QuarterMph** is NaN) |
| 6 | If it hooks up | answer `Show(QuarterEt with the lowest-EtConstant formula, 2)` | | `seconds on {best.Name} — a run that hooked up` |
| 7 | If it does not | answer `Show(QuarterEt with the highest-EtConstant formula, 2)` | | `seconds on {worst.Name} — one that did not` |

(Hale has EtConstant 5.825 and Huntington 6.290, so in practice "best" is Hale and "worst" is Huntington.)

**READ A TIMESLIP**

| Row | Label | Control | Default | Unit / note |
|---|---|---|---|---|
| 0 | Trap speed | field | `115` | `mph at the far end` |
| 1 | Elapsed time | field | `13.0` | `seconds over the quarter` |
| 3 | Power it made | answer 19 pt `Show(PowerFromTrap,0)` | | `hp — type a trap speed above` (PowerFromTrap is NaN); `hp at the crank, from the trap speed` (PowerFromEt is NaN); else `hp from the trap — the time on its own would say {PowerFromEt:N0}` |
| 4 | The launch | answer: `{LaunchCost:+0.00;−0.00}`, where the negative section uses U+2212 MINUS SIGN and zero prints `+0.00`; `—` if NaN | | `seconds against the {EtTheTrapDeserved:N2} that trap deserved`, or `seconds — type a time as well` |

The reading is `DragStrip.Read(trap, et, weight, chosen formula)` (core). The verdict note uses LaunchWorthMentioning = 0.30:
- LaunchCost is NaN: `Type in a trap speed and a time from a slip and this will read the run: what the car actually made, and what the start line cost.`
- LaunchCost > 0.30: `The car trapped like {PowerFromTrap:N0} hp and ran {LaunchCost:N2} s slower than that trap deserved. That time went missing in the first sixty feet — tyres, pressure, launch rpm or suspension — and it is the cheapest time on the car to find. Nothing done to the engine will show up until it is.`
- LaunchCost < −0.30: `The car ran {−LaunchCost:N2} s quicker than its trap speed deserved, which means it left very well indeed — or that it weighs less than the figure above. The trap is still the honest read on power.`
- Otherwise: `The time and the trap agree: about {PowerFromTrap:N0} hp, and a launch with nothing much left in it. Further time now has to come from power or from weight.`

Static notes:
1. `Trap speed and elapsed time are not equally worth trusting. By the far end a car has had a quarter of a mile to forget a bad launch, so the speed it arrives at is very nearly a measure of power against weight. The time does not forget — a wheelspin in the first sixty feet is in it for good. So the trap is read for power, and the time is read for the start line.`
2. `These are correlations fitted to thousands of real runs, not physics. There is no term here for traction, gearing, the air, the track or the driver — any of which moves a quarter-mile time by more than the differences between the three formulas. Every figure is the middle of a wide distribution.`

#### Page: Fuel economy (running costs)

Intro: `Three vehicles side by side: what each costs to run by the week, the month and the year.`

The **Miles a year** row sits in its own grid (label column 150, bottom margin 10):
- field, default `12000` (`F0` of TypicalAnnualMiles);
- note: `{miles/WeeksPerYear:N0} a week, {miles/12:N0} a month` if > 0, else `how far you drive in a year`.

**Comparison grid.** The label column is 150. Then three equal-share columns, each at least 110 wide; there is a 6 px gap on the right of columns A and B. The fields here are full column width (not 90), right-aligned, with the same focus behaviour.

| Row | Label | Per column (A, B, C) |
|---|---|---|
| 0 | (none) | headings `VEHICLE A`, `VEHICLE B`, `VEHICLE C` (Heading style, margin 0,0,0,4) |
| 1 | Fuel | combo of `Fuels.All` names: `Petrol`, `Hybrid (petrol)`, `E85`, `Diesel`, `CNG`, `Electric`. Defaults A = `Petrol`, B = `Hybrid (petrol)`, C = `Electric` |
| 2 | Economy | field. Defaults A `26`, B `48`, C `3.5` |
| 3 | Price | field. Default = `FuelPrices.For(kind)` formatted `0.####` |
| 5 | A week | answer `Money(PerWeek,0)` |
| 6 | A month | answer `Money(PerMonth,0)` |
| 7 | A year | answer 19 pt `Money(PerYear,0)` |
| 8 | Per mile | Unit style, right-aligned. If usable: `{CostPerMile×100:N1}¢ · {Show(UnitsPer(miles),0)} {UnitPlural} a year`; else `{EfficiencyUnit}, ${Unit}`, for example `mpg, $gallon` or `mi/kWh, $kWh` |
| 10 | Energy, as MPGe | Unit style: `{Mpge:N0} MPGe` if usable, else `—` |

Rows 4 and 9 are 10 px spacers. The vehicle record is `VehicleCost(name "A"/"B"/"C", kind, economy, price)` (core).

**Fuel changed:** that column's price is **overwritten** with the new fuel's average price (`0.####`), without the overwrite counting as typing. Then the page recalculates.

**Verdict box** (full width, padding 12,9):
- **Verdict text** (semibold, `Text`, wrapped):
  - No usable vehicle, or miles ≤ 0: `Fill in the economy and the price for at least one vehicle, and how far you drive in a year.`
  - Exactly one usable vehicle: `Vehicle {X} costs {Money(PerYear,0)} a year in {fuel name lower-cased} over {miles:N0} miles. Fill in another to compare.`
  - Two or more:
    - Template: `Vehicle {best} is the cheapest to run — {list}. Over {miles:N0} miles a year.`
    - best = `RunningCosts.Cheapest`.
    - list = the items `{Money(AnnualSaving(best, v),0)} a year less than {v.Name}`, one per other usable vehicle, joined with `, and `.
- **Warning line** (11 pt, `Marker`, top margin 7): shown only when some usable vehicle passes `RunningCosts.LooksCopiedFromPetrol(v, usable)`; otherwise hidden. The first such vehicle fills the text: `Vehicle {X} is on E85 at {Economy:N0} mpg, which is close to what a petrol car in this comparison manages. E85 holds about three-quarters of petrol's energy, so the same car does roughly a quarter fewer miles to the gallon on it. As entered, E85 will look cheaper than it is.`

After the box:
- Heading `ABOUT THESE FUELS`. Below it, a note with no top margin: one paragraph per **distinct** chosen fuel kind, in column order, each `{Name} — {Note}` (core), joined by a blank line.
- Heading `WHERE THE PRICES CAME FROM`. Below it, a note with no top margin: `FuelPrices.Source` (core).
- Static note: `Fuel prices move week to week and differ by dollars between states, so the figures above are a starting point rather than an answer. Type in what you actually pay and the comparison becomes about your cars rather than about the national average. This counts fuel and electricity only — not tyres, servicing, insurance or depreciation, any of which can be larger than the difference shown here.`

---

### Estimate power window

#### Frame

- Title `Estimate power`. Initial size 700 × 720, minimum 560 × 460. Centred on the main window. No taskbar entry. Background `Bg`.
- The window is created and shown with a reference to the main view model. It computes against `MainViewModel.Document`, the currently open log.
- **Bottom strip:** background `Panel`, top border 1 px `Line`, padding 12,7.
  - Right: `Close` (80 wide; closes the window).
  - Left of Close, 8 px gap: `Add channels` (110 wide).
  - Left: a status line (11 pt, `Muted`, ellipsis trim).
- **Body:** a vertically scrolling single column with margin 16. Its styles match the calculators: fields 90 wide, right-aligned; label column 170; unit notes 11 pt `Muted`; headings 11 pt semibold `Muted` with margin 0,14,0,4.
- The field focus behaviour (select all on focus; the first click only focuses) is the same as in the calculators.

#### Content, top to bottom

1. Intro note (bottom margin 4, no top margin): `Two independent estimates from what the log already holds. Speed density works the air out from the manifold; the injector route ignores the air and counts the fuel instead. Where they agree the figure is worth believing — where they do not, the disagreement is the useful part.`

2. Heading `THE ENGINE`:

| Label | Control | Default | Unit / note |
|---|---|---|---|
| Displacement | field | `2.0` (`N1`) | `litres` |
| Cylinders | field | `4` | `one injector each` |
| Fuel | combo, 150 wide, fuel list (ten long names) | Petrol | `stoichiometric {stoich:N2}:1, {density:N3} kg/L` |
| BSFC | field | `0.50` (`N2`) | `lb/hp/hr — {SuggestedBsfc(fuel, 0.50):N2} on {ShortName} at full throttle` |
| Volumetric efficiency | field | `95` (`N0`) | no log: `%`; if any available method's basis contains `VE from`: `% — ignored, the log reports its own`; else `% — used, since the log does not report it` |
| Driveline loss | field | `0` | `% — zero leaves the figure at the crank` |

3. Heading `THE INJECTORS`:

| Label | Control | Default | Unit / note |
|---|---|---|---|
| Injector flow | field | `550` (`N0`) | `cc/min, each` |
| Rated at | field | `300` (`N0`) | `kPa across the injector — 300 is three bar` |
| Dead time | field | `1.00` (`N2`) | `ms — {dt:N2} ms is {dt×7000/1200:N1}% of the cycle at 7,000 rpm` |

   Then two check boxes spanning the row, text `Text`, top margins 6 and 4, both off by default:
   - `Batch injection — fires twice a cycle rather than once`
   - `Logged fuel pressure is already the difference across the injector`

   The defaults are `N1`, `N0` and `N2` in the current culture, so thousands separators appear for large values.

4. Heading `WHAT THIS LOG CAN DO`. A full-width panel box (padding 12,8, corner `CornerMedium`) holds two wrapped 11 pt text blocks:
   - **Available** (`Text`): one line per method in the estimate, `✓  {Name} — {Basis}` (two spaces after ✓), joined by newlines. If there are no methods: `Nothing here can be estimated from this log.`
   - **Missing** (`Muted`, top margin 8): one line per unavailable method, `—  {Name} needs {Needs}` (em dash, two spaces), or empty.
   - Method names and basis texts come from the core `PowerEstimate.For`: `Speed density`, `Injectors`, `Mass air flow`, `Agreement`, `Torque`, `At the wheels`.

5. Static note: `None of this is a dyno. Every method multiplies by a brake specific fuel consumption that has been assumed rather than measured, so the absolute number carries that assumption's error — ten per cent, easily. What they are good for is shape and change: where the power is made, what a modification did, whether two runs match. Those are ratios, and the assumption divides out of a ratio.`

6. Advice note:
   - Shown only when one of the methods is named `Agreement`: `Both routes are available on this log, so watch the spread channel. A few per cent apart is noise. A steady gap means one of the two inputs is wrong — an optimistic VE table pushes the air estimate up, and injector data that is not what the box claimed pushes the fuel estimate about. The fuel consumption you assumed cancels out of the comparison, so the spread is telling you about the engine rather than about the guess.`
   - Otherwise it is empty.

#### Behaviour

- **Recalculation.** Every keystroke in any field, every check box click and every fuel change rebuilds the spec and recalculates.
- **Parsing.** Fields are parsed in the current culture. An unparsable value falls back to the `EngineSpec` default for that field: litres 2.0, cylinders 4, BSFC 0.50, VE 95, injector 550, rated 300 kPa, dead time 1.0, loss 0. Cylinders are truncated to an integer. There are no error messages.
- **Spec.** The spec also carries the default Lambda of 0.85, which has no field.
- **Estimate.** estimate = `MainViewModel.EstimatePower(spec)`, which returns `PowerEstimate.For(Document, spec)`, or nothing when no log is open.
- **No log open:**
  - Available `No log is open.`; Missing empty; Advice empty.
  - VE note `%`.
  - `Add channels` disabled.
  - Status `Open a log first.`
- **Log open:**
  - `Add channels` is enabled exactly when the estimate has ≥ 1 channel.
  - Status: `{count} calculated channel(s) to add.`, or `Nothing to add from this log.`
- **Add channels clicked:** calls `MainViewModel.AddPowerChannels(spec)`. That:
  1. Returns 0 if there is no log or no channels.
  2. Otherwise removes every existing math channel whose name matches (case-insensitive) one of the new channels, so a repeat replaces rather than duplicates.
  3. Adds the new channels to the math-channel store, refreshes the channel list and re-applies.
  4. Sets the main hint to `Added {n} channels from {method names lower-cased, joined with " and "}.`
  5. Returns n.

  The window's status then becomes `Added {n} channel(s). Tick them in the channel list to plot them.` (n > 0), or `Nothing was added.`
  - The added channels are **not** made visible on the plot automatically.
  - This changes only the in-memory calculated channels of the open log. **No ECU, tune or file write.**
- **Keyboard and mouse.** No shortcuts. No Enter or Esc handling beyond the system defaults.

---

### Open questions

1. **Cross-tab values go stale** (bug; a builder can settle it, recommend fixing).
   - Several pages read fields from other pages, but a change on the source page recalculates only that page:
     - the Engine page's "With your boost" reads the Boost tab's MAP;
     - the Engine recipe reads the Pressure ratio tab's inlet and charge loss, and the Fuel pump tab's base rail pressure;
     - Turbo sizing reads the Pressure ratio tab's charge loss.
   - The dependent page refreshes only when one of its own fields changes, or when barometric pressure changes (which recalculates all).
   - Recommendation: after any change, recalculate every page whose inputs include the changed field, or simply recalculate all.
   - The user may prefer the exact WPF behaviour for fidelity, so this needs their decision.
2. **The power window never refreshes on its own** (bug; needs the user's decision). If the open log is closed or replaced while the window is open, the "what this log can do" text and the button state stay stale until a field is edited. Recommend a refresh when the document changes.
3. **`--power` ignores the menu's enabled rule.** It opens the window with no log; the window then shows the no-log state. Harmless; a builder can settle this by keeping it.
4. **Unmatched `--calculators` name.** It logs the error and still writes an image of the first page. This is deliberate in the source; keep it.
5. **`— °F` for invalid temperatures** on the Intercooling page (a dash followed by the unit). A builder can settle this; keep it for fidelity.
6. **Gear ratios are split on comma** even in comma-decimal cultures. A builder can settle this.
7. **The Injectors page does not carry the BSFC across a fuel change**, while the Pump and Turbo pages do. This looks deliberate (the hint text changes) but is inconsistent. The user would decide.
8. **Misplaced doc comment.** The Turbo page's fuel-change comment describes carrying both values, and it is attached to the VE-family handler in the source. Behaviour is as specified above; this is informational only.
9. **Boost tab charge density** prints zero and negative values, while most answers blank values ≤ 0. Faithful port; a builder can settle this.
10. **Unreachable empty table.** The fuel economy "Per mile" fallback text `{EfficiencyUnit}, ${Unit}` reads oddly (`mpg, $gallon`). Faithful port; the user may want it reworded.

### Safety rules found

- **Calculators window: none.** No calculator writes to the tune, the ECU, a file or the log, and there is no apply, push or send-to-settings action of any kind. Every output is display only. The injector size, the VE figures and the cam data are never offered to the tune.
- **Estimate power, `Add channels`.** This adds or replaces calculated (math) channels in the **in-memory open log** only, through `MainViewModel.AddPowerChannels`, and replaces same-named channels without asking. It needs no confirmation. Its status text is `Added {n} channel(s). Tick them in the channel list to plot them.` and the main hint is `Added {n} channels from {methods}.` It never touches the ECU or a tune. Listed here because it silently replaces existing same-named math channels.


## §7 `w8-viewmodel`: application state, agent bridge, tuning projects

### Scope of this section

This section is the shared application state model (Rust module `olv::state`), the agent/MCP bridge, and the Tuning project window. It is drawn from `MainViewModel.cs` (and its partial files `AgentCommands.cs`, `ProjectCommands.cs`, `SavedTuneCommands.cs`), `AgentBridge.cs`, `ProjectWindow.xaml(.cs)`, and from the Core classes those call (`AgentServer`, `AgentActivityLog`, `LiveSubscriber`, `TuningProjectStore`, `TuneHistory`, `Workspace`, the `*Store` classes) and the MCP translator `tools/OpenLogViewer.Mcp/Program.cs`.

The ECU write, burn, restore and connect paths (`WriteSettingsToEcu`, `WriteTableToEcu`, `BurnSettingsToEcu`, `BurnTableToEcu`, `WriteCurveToEcu`, restore planning, the `Connect*` family) are **not** specified here. Where a rule here depends on them it says "see Safety".

Number formats used below are .NET format strings: `N0` = integer with thousands separators ("1,234"), `F1` = one decimal, `F0` = no decimals, `0.###` = up to three decimals with no trailing zeros, `G6` = general with 6 significant digits. Dates use .NET custom formats (`yyyy-MM-dd_HH-mm-ss` and so on).

---

### 1. The application state model

#### 1.1 The core state values

All screens read one shared view-model object. Every value below is observable: when it changes, everything showing it updates.

| State (`MainViewModel.` name) | Type | Initial | Meaning / set by |
|---|---|---|---|
| `Document` | log or none | none | The log on screen. Set by **Open log** (`Load`) and, while live, replaced on every live tick by the newest live snapshot (`RefreshLive`). Changing it re-evaluates `HasDocument`, `CanExport` and `CanExportPlotted`. |
| `HasDocument` | bool | false | `Document is not none`. |
| `Channels` | ordered list of channel rows | empty | One row per non-time channel of the document, in file order, then calculated (math) channels. Each row has `IsVisible` (plotted), colour, `IsFlat` (never moves), `IsCalculated`, category, units, a cursor readout and a selection readout. |
| `ChannelView` | filtered and sorted view of `Channels` | | What the channel list shows. See 1.4. |
| `Presets` | list | from disk | Saved channel selections, sorted by name, case-insensitive. |
| `Hint` | string | `DefaultHint` (below) | The status-bar message: the result of the person's last action. See 1.6. |
| `Status` | string | `Ready — open a .mlg, .msl or .csv datalog` | The toolbar's flexible middle text (what is open). |
| `Title` | string | `OpenLogViewer` | The window title. |
| `CursorTime` | string | `—` | Status-bar readout of the cursor or the marked span. |
| `FilterSummary` | string | "" | Under the channel filter bar. |
| `Mode` | `Log` / `Gauges` / `Calibration` / `Guide` | `Log` | Top-level workspace mode. `InLogMode`, `InGaugeMode`, `InCalibrationMode` and `InGuideMode` are each true when `Mode` is that value. Setting one of them to true sets `Mode`. Setting one to false does nothing. |
| `LogView` | `Plot` / `Histogram` / `Scatter` | `Plot` | How the Log mode shows the log. `ShowLog`, `ShowHistogram` and `ShowScatter` follow the same rule as the `In*Mode` values. `ShowAxisPanel` is `LogView` is Histogram or Scatter. |
| `StackedLanes` / `OverlaidLanes` | bool | false / true | Exact opposites. Setting `OverlaidLanes` to true sets `StackedLanes` to false. |
| `Search` | string | "" | Channel search text. |
| `HideUnused` | bool | **true** | Hide channels that never move. |
| `Sort` | `Category` / `Name` / `Plotted` | `Category` | `SortByCategory`, `SortByName` and `SortByPlotted` follow the same rule as the `In*Mode` values. |
| `Selection` | (first, last) sample span or none | none | The marked span (Shift-drag on the plot). |
| `IsLive` | bool | false | A live session exists **and** is running. |
| `LiveSignature` | string | "" | The firmware signature of the connected ECU. `MaxxECU` for a MaxxECU. Cleared on disconnect. |
| `LiveStatus` | string | "" | Status-bar live indicator. See 1.5. |
| `LiveHealthy` | bool | true | False while the link is down and the app is waiting for it to come back. |
| `LiveDetail` | string (multi-line) | `Not connected.` | Tooltip on the live indicator. See 1.5. |
| `IsRecording` | bool | false | The live session is writing a file. |
| `RecordingPath` | string | "" | The file being written, otherwise the last one written this session, otherwise "". |
| `HasEcuTune` / `NoEcuTune` | bool | false / true | An ECU tune is in hand (read off a controller, opened from a `.msq`, or a placeholder from a definition). |
| `TuneIsPlaceholder` | bool | false | The ECU tune came from a definition file alone. All of its values are zeros. |
| `TuneIsFromFile` | bool | false | The ECU tune was opened from a saved `.msq`, not read off the controller. |
| `ShowNoTuneNotice` | bool | true | `NoEcuTune AND NOT IsObd2Live`. |
| `IsObd2Live` | bool | false | The session is OBD2 **and** `IsLive`. |
| `TuneSource` / `TuneDetail` / `TuneWarning` / `HasTuneWarning` / `UsingLoadedTune` | | | The **log-analysis** tune (a `.msq` opened to supply table axes and VE values). It is separate from the ECU tune. See 1.3. |
| `AgentIsRunning`, `AgentWritesArmed`, `AgentIsActive`, `AgentIsWritingToEcu`, `AgentLastAction`, `AgentSummary`, `AgentToggleLabel`, `AgentAddress` | | | The agent bridge. See section 2. |
| `Project`, `HasProject`, `ProjectSummary` | | | The open tuning project. See section 3. |
| `DefinitionNeeded` | description or none | none | Set when a connect fails because no definition matches. Cleared when a connect succeeds. |

`DefaultHint` (exact text):

```
Scroll = zoom  •  Drag = pan  •  Double-click = fit  •  Hover a trace for its min/max, then click either to jump there  •  Shift-drag to mark a span and summarise it  •  Right-click for more
```

(The separators are two spaces, `•`, two spaces.) `ResetHint()` puts this text back.

**Startup note:** the agent API starts on every launch (section 2.1), and its result message is written to `Hint`. In practice the first hint a person sees is therefore `The agent API is listening on http://127.0.0.1:8765. Its token is in {path}.`, not `DefaultHint`.

**Two kinds of "tune".** The builder must keep these apart:

1. The **ECU tune** (`HasEcuTune` and the related values). It is what Calibration shows and edits, and what the agent API reads and writes. It comes from three places: read off a live controller at connect, opened from a saved `.msq` ("from file"), or built from a definition `.ini` alone ("placeholder").
2. The **log-analysis tune** (`TuneSource` and the related values). It supplies table axes and VE values for the histogram and VE Calibration. It is either the tune embedded in the log, or a `.msq` opened by hand (File → Open tune…, or dropping a `.msq`).

#### 1.2 Every derived enabled / visible rule

Rules are exact. "Connection" means a live ECU connection that can be written to: a TunerStudio-protocol connection (`_ecuConnection`). A MaxxECU over USB has no such connection (see Safety).

| Property | Exact rule | Used for |
|---|---|---|
| `HasDocument` | a document is open (log or live snapshot) | menu items that need a log: Compare against another log…, Estimate power… |
| `CanExport` | `Document` is not none | export menu items |
| `CanExportPlotted` | at least one channel row has `IsVisible` | "export plotted channels" |
| `ExportScope` | marked span: `marked span, {n:N0} samples` where n = last − first + 1; otherwise `whole log` | shown in the export menu |
| `CanRecord` | `IsLive` | Record button visible; Record menu item enabled |
| `IsRecording` | session is writing | Record button turns `Marker` colour and SemiBold |
| `RecordLabel` | `IsRecording` ? `■ Stop recording` : `● Record…` | Record button and menu item text |
| `CanReconnect` | `NOT IsLive AND LastConnected is not none` | the one-click reconnect shortcut |
| `ReconnectLabel` | with a last device: `Connect: {name}`, where name is the remembered ECU signature cut to its first two space-separated words (or the whole signature if it has two words or fewer), or the port name if no ECU is remembered; with no last device: `Connect` | the shortcut's label |
| `LastConnected` | of the serial ports present right now that are known and not "incoming" Bluetooth ports, the one with the latest last-used time (ports with no time sort last) | |
| `ShowNoTuneNotice` | `NoEcuTune AND NOT IsObd2Live` | Calibration's "no tune" notice |
| `IsObd2Live` | OBD2 session AND `IsLive` | Tools → Fault codes… enabled |
| `HasSettingChanges` | the settings edit has changes | |
| `CanWriteSettings` | `HasSettingChanges AND connection AND definition layout loaded AND NOT TuneIsPlaceholder AND NOT TuneIsFromFile` | see Safety |
| `CanBurnSettings` | `at least one settings page written this session AND connection AND NOT TuneIsPlaceholder AND NOT TuneIsFromFile` | see Safety |
| `HasTableChanges` | the table edit has changes | |
| `CanWriteTable` | `HasTableChanges AND connection AND layout AND NOT TuneIsPlaceholder AND NOT TuneIsFromFile` | see Safety |
| `CanBurn` | `connection AND layout AND NOT TuneIsPlaceholder AND NOT TuneIsFromFile` | see Safety |
| `HasCurveChanges` | any open curve has changes | |
| `CanWriteCurve` | `HasCurveChanges AND connection AND layout AND NOT TuneIsPlaceholder AND NOT TuneIsFromFile` | see Safety |
| `CanSaveTune` | `ECU tune in hand AND layout AND NOT TuneIsPlaceholder` (a tune from a file **can** be saved) | File → save tune |
| `CanApplyRestore` | `a non-empty pending restore AND connection AND NOT TuneIsPlaceholder AND NOT TuneIsFromFile` | see Safety |
| `HasSettingsPages` | the settings menu built from the definition has at least one entry | |
| `ShowSettingsPages` / `ShowEcuTables` | exact opposites (`_showSettings`) | Calibration's two halves |
| `ShowSettingsPagesOnly` | `ShowSettingsPages AND NOT HasOpenCurves` | |
| `ShowSettingsFields` | `ShowSettingsPages AND a page is open AND that page has at least one visible field` | |
| `ShowCurves` | `ShowSettingsPages AND HasOpenCurves` | |
| `ShowTableView` | `ShowEcuTables OR (ShowSettingsPages AND the open menu entry is a table)` | |
| `HasGauges` / `NoGauges` | the gauge list is non-empty / empty | |
| `HasTuneAxes` | the axis-source list has more than one entry (the first is always "from the log data") | |
| `UsingTuneAxes` / `UsingDataAxes` | the histogram bins come from tune axes / from the data | |
| `NeedsSecondValue` | the chosen filter comparison takes two values | |
| `VeAvailable` | `Document AND XAxis AND YAxis are all set` | |
| `CanStepFindings` | the last search found at least one run | |
| `HasComparison` / `NoComparison` | a second log is / is not open | |
| `HasMathChannels`, `HasMathProblems`, `HasTuneWarning`, `HasStyleUnits`, `EditingStyle`, `HasVeDelayFinding`, `HasTableChangePreview` | the matching list or string is non-empty, or the target is set | |
| `UsingLoadedTune` | a `.msq` has been opened by hand for log analysis | status-bar tune readout turns `Accent` |
| `HasTuneWarning` | `TuneWarning` is non-empty | status-bar tune readout turns `Marker` (this wins over `Accent`) |

After any change to the connection, the tune source or the placeholder/from-file flags, all the write gates are re-evaluated together (`RaiseWriteGates`): `CanWriteTable`, `CanWriteSettings`, `CanWriteCurve`, `CanBurn`, `CanBurnSettings`, `CanSaveTune`, `CanApplyRestore`, `TuneIsPlaceholder` and `TuneIsFromFile`. The Rust state must recompute these from the base state, not cache them.

**Disconnect** (`Disconnect()`, which does nothing when there is no session) does the following, in order:

1. Stops and disposes the session.
2. Drops the ECU connection, the MaxxECU USB source and its definitions, and the OBD2 source and its undecoded list.
3. Sets **`AgentWritesArmed = false`**, which also sets the Hint shown in 2.3.
4. Clears `LiveStatus` and the port, signature, version, INI and recording-path strings.
5. Re-evaluates every derived value above.
6. If a document is still on screen, sets `Hint` to `Disconnected. The session is still here, and its recording is on disk.`

The ECU tune and its tables are **not** cleared on disconnect. After a disconnect they are simply not writable, because there is no connection.

#### 1.3 Log-analysis tune readouts

`TuneSource` is shown in the status bar as `Tune: {TuneSource}` (max 200 px wide, cut off with an ellipsis). It is also shown in the histogram panel at font size 10.

| Condition (checked in order) | `TuneSource` | `TuneDetail` (tooltip) |
|---|---|---|
| a `.msq` was opened by hand | the file name | `Tables come from {file}. Right-click to go back to the log's own tune.` |
| the log embeds a tune | `from the log` | `Tables come from the tune stored in this log — the one that was running when it was recorded.` |
| the log carries a tune in a format the app cannot read (e.g. MaxxECU) | `{format}, unreadable` | `This log carries its {format} tune, in a format this cannot read. Axis breakpoints and VE Calibration need a tune it can, so both are unavailable for this log.` |
| otherwise | `none` | `This log carries no tune. Open a .msq to bin onto its table axes and to use VE Calibration.` |

Colour: `Faint` by default, `Accent` when `UsingLoadedTune`, `Marker` when `HasTuneWarning` (this one wins).

`LoadTune(path)`:
- If the file has no axis sets, it fails with `No usable tables found in that file. It should be a TunerStudio .msq tune.`
- Otherwise it is used, and `TuneWarning` is worked out by comparing the opened file's `VE table 1` cell by cell with the embedded tune's `VE table 1` (tolerance 1e-6, and the column and row counts must match). If they differ: `This tune's fuel table differs from the one stored in the log. VE Calibration scales the numbers that produced the logged AFR, so suggestions will be off by however much the tune has changed since the drive.` If either table is missing, there is no warning.
- `Hint` is set to the warning if there is one, otherwise to `Using tables from {file}.`

`ClearTune()` does nothing unless a tune was loaded. Otherwise it clears the tune and the warning and sets `Hint` to `Back to the tune stored in the log.`

Both rebuild the axis-source list and reset the axis source to "from the data". They also turn VE Analyze off (see the histogram section).

#### 1.4 Channel list rules

- **Filter.** If `Search` is non-empty, a row is listed when its name, units or category name contains the search text (case-insensitive). The search overrides `HideUnused`. Otherwise a row is hidden only when `HideUnused AND row.IsFlat AND NOT row.IsVisible`, so a plotted channel is always listed.
- **Sort.**
  - `Category`: grouped by category name, sorted by category order, then by name.
  - `Plotted`: visible rows first, then by name.
  - `Name`: by name.
- **`FilterSummary`.** Empty when there are no channels. Otherwise `{shown}/{total} · {plotted} plotted`, with ` · {hidden} hidden` added when hidden > 0.
- **Default plotted channels when a log loads:** `RPM`, `MAP`, `TPS`, `AFR` and `CLT`, matched by name (case-insensitive), and only those that are not flat. If none of them match, the first four non-flat channels are plotted. On a live session the same five names are ticked even if they are flat.
- **Colours.** Each row is first given the next palette colour in file order. When a row becomes visible and has no pinned colour, it takes the first palette colour no other visible row is using. If every colour is in use, it takes the next one in rotation.
- **Bulk changes** (presets, Show all / none, Plot common) are applied as one update, with one redraw.
  - `SetAllVisible(true)` ticks only the rows the filter currently lists.
  - `SetAllVisible(false)` unticks every row.
  - `PlotCommon()` shows exactly the rows in the Common category that are not flat.

#### 1.5 Live status texts

These are set at connect by each connect path (see Safety for the connect itself).

| Path | `Status` | `Title` |
|---|---|---|
| TunerStudio-protocol ECU | `Live — {signature}   •   {n} channels   •   {ini file name}` | `Live: {signature} — OpenLogViewer` |
| MaxxECU (Bluetooth/CAN) | `Live — MaxxECU   •   {n} channels` | `Live: MaxxECU — OpenLogViewer` |
| MaxxECU USB | `Live — MaxxECU (USB)   •   {n} channels` | `Live: MaxxECU ({where}) — OpenLogViewer` |
| Subaru SSM | `Live — SSM   •   {n} parameters   •   {adapter}` | `Live: SSM — OpenLogViewer` |
| OBD2 | `Live — OBD2   •   {n} channels   •   {signature}` | `Live: OBD2 — OpenLogViewer` |

(The separators are three spaces, `•`, three spaces.)

**Connect hint.** Every path starts with the **opening sentence**:
- recording: `Recording to {path}.`
- not recording: `Watching only — nothing is being written. Press Record when you want a log.`

Then it adds path-specific text:
- TunerStudio: ` The plot follows the newest data until you zoom or pan.` If there is at least one channel, add `  Untick "Hide unused" to see every channel — all of them are being recorded either way.` If two or more definitions declare this signature, add `  {k} definitions on this machine declare this signature ({names, comma-separated}); this session is using {ini file name}.`
- MaxxECU: ` A MaxxECU sends a fixed set of channels, and its tune cannot be read, so calibration is not available for it.`
- MaxxECU USB: ` Over USB a MaxxECU names every channel it sends, so this session found {count} of them by listening rather than by being told. {tuneNote}`
- SSM: ` Reading {n} parameter{s} over Subaru's own protocol — one address per request, so this updates about once a second. That suits what SSM is for: values the ECU has learnt, which move over minutes. Edit {SSM parameter file name} in the definitions folder to change what is read.`
- OBD2: one of two sentences, depending on whether the car batches requests.
  - batching: `This car answers several parameters to one request, so a round of readings is two exchanges rather than six — still slower than a tuning cable, and much better than OBD2 usually manages. `
  - not batching: `OBD2 asks for one parameter at a time, so this updates about twice a second rather than 25 times — the protocol's limit, not the link's. `

  Then: `A standard vehicle has no tune to read, so calibration shows its fault codes instead.` If any parameters are undecoded, add `  {Obd2Gaps}.`

**`LiveStatus`** is recomputed on every live tick. The UI drives the tick with a timer.

| Session state | Text |
|---|---|
| faulted | the session's error text |
| reconnecting | `○ {port} · waiting for the ECU to come back…` |
| otherwise | `● {port} · {signature} · {rate:F1} Hz`, then ` · {retries} retries` if retries > 0, then ` · {written}` |

`{written}` is `REC {rows:N0} rows` while recording, otherwise `not recording`.

The live indicator is shown only while `IsLive`. Its colour is `Accent`, or `Marker` when `LiveHealthy` is false. It is max 320 px wide and cut off with an ellipsis.

If the session faults: disconnect, then set `Hint` to the session's error text.

**`LiveDetail`** (the tooltip) is `Not connected.` when not live. When live it is six lines, with labels padded so the values line up:

```
Port      {port}
Firmware  {signature}
Build     {version}
INI       {ini path}
Channels  {count}[  (+{k} not decoded)]
Recording {path or "not recording"}
```

**Opening a log while live.** `Load` replaces `Document`, but the session keeps running, and the next live tick replaces `Document` with the live snapshot again. **Open question:** whether opening a saved log while connected should end the session, pause the live refresh, or be refused. The C# does none of these (see Open questions).

#### 1.6 Status-bar (`Hint`) messages set by the view model

These are the exact texts. The messages owned by write, burn, restore and connect are not repeated here (see Safety). Messages owned by the histogram, VE, filter and math screens are listed so the state builder can provide them. Their screens' sections may give more context.

| Trigger | Exact `Hint` text |
|---|---|
| Data folder moved | `Recordings and exports now go to {root}.` |
| Style edit: unparsable min/max | `A pinned scale needs two numbers, or neither to go back to automatic.` |
| Pin range: low ≥ high or not finite | `A pinned scale needs a low value below a high one.` |
| Style store full (smoothing) | `There is no room left to remember another channel's settings. Clear one that is pinned and try again.` |
| Style store full (colour) | `There is no room left to remember another channel's colour. Clear one that is pinned and try again.` |
| Style store full (range) | `There is no room left to remember another channel's scale. Clear one that is pinned and try again.` |
| Smoothing off | `{name} is drawn as logged again` |
| Smoothing on | `{name} is drawn smoothed — a median of {window} samples. Measurements still use it as logged.` |
| Colour cleared / pinned | `{name} takes the scheme's palette again` / `{name} is pinned to this colour in every log` |
| Range cleared | `{name} is scaled to its own range again` |
| Range pinned | `{name} is drawn over {min} … {max} in every log` (min and max formatted by the channel's own formatter in the current unit system) |
| Style cleared | `{name} is back to the scheme's colour, its own range and as logged` |
| Add filter: no channel / bad value / missing second value | `Pick a channel for the filter.` / `Enter a number for the filter value.` / `This comparison needs two values.` |
| Gauge peaks reset | `Peak and low markers cleared.` |
| Single request block on | `The realtime block will be asked for in one request from the next connection. If the ECU stops responding, unplug it, switch this back off, and reconnect.` |
| Single request block off | `Back to reading the realtime block in blocking-factor pieces.` |
| Logging rate changed | live: `Logging rate set to {rate:N0} Hz — it applies to the next connection.`; not live: `Logging rate set to {rate:N0} Hz.` The rates offered are 5, 10, 25, 50, 100 and 200. |
| MaxxECU gauges lack MTune definitions | the problem text from the gauge loader |
| Marked span | `Marked {from:F2}–{to:F2} s. Rows show min … max and average over the span. Click the plot to clear.` (and `CursorTime` = `{to−from:F3} s selected   ({count:N0} samples)`) |
| Span cleared | `DefaultHint` (and `CursorTime` = `—`) |
| Cursor moved | `CursorTime` = `{time:F3} s   (sample {index:N0})` (`Hint` is unchanged) |
| Log CSV export | `Saved {n} channel{s} over the {ExportScope} to {file name}` |
| Image export (done by the window) | `Saved the {what} to {file name}` |
| Table CSV export | `Saved the {columns}×{rows} table to {file name}` |
| Points CSV export | `Saved {count:N0} points to {file name}` |
| Save preset: nothing plotted | `Plot some channels first, then save them as a preset.` |
| Save preset ok | `Saved preset “{name}” — {n} channels.` or `Updated preset “{name}” — {n} channels.` (curly quotes U+201C/U+201D) |
| Save preset fails | the store's error message |
| Apply preset: no match | `“{name}” names no channel in this log, so nothing changed.` |
| Apply preset ok | `Applied “{name}” — {n} channels.` or `Applied “{name}” — {matched} of {total} channels; {missing} not in this log.` |
| Delete preset | `Deleted preset “{name}”.` |
| Histogram cell traced | `{X name} {x:G6} · {Y name} {y:G6} — {value} from {samples:N0} samples over {one visit | {k} visits, all marked}. Showing the longest ({n:N0} samples).` |
| Scatter block traced | `{X} {x:G6} · {Y} {y:G6} — {Z name} {mean}[ ({low} to {high})] from {count:N0} samples over {…}. Showing the longest ({n:N0} samples).` |
| Math channel added / removed | `Added the calculated channel "{name}".` / `Removed "{name}".` |
| Known devices forgotten (returned to the window) | `No devices were remembered.` or `Forgot {n} remembered device{s}. Presets, filters and calculated channels are untouched.` |
| Agent arming changed | see 2.3 |
| Agent installed a definition | `An agent installed the firmware definition {name} ({signature}) from {source}. Connect again to use it.` |
| Burn recorded into project | `Kept as {id}. …` (see 3.4) or `Burned, but the project could not record it: {error}` |

The window also sends some results through `Hint` (via `SetHint`), for example the agent API on/off messages in 2.1 and the project messages in 3.6. Every such message is also appended to a run log file in the temp folder, `openlogviewer-run.log`, as `HH:mm:ss  {message}`.

#### 1.7 Opening a log

- **Entry points:**
  - File → Open log… (Ctrl+O). Dialog title `Open datalog`. The file must exist.
  - Dropping a file on the window. If the first dropped file has the extension `.msq` (case-insensitive), it is opened as a log-analysis tune instead of a log.
  - A command-line argument. The first argument that is neither a switch nor a switch's value is the log path.
- **File-type filter** (in this order):

| Label | Patterns |
|---|---|
| `All datalogs` | `*.mlg;*.msl;*.csv;*.txt;*.log;*.tsv;*.dat;*.MaxxECU-Zip-log` |
| `Binary log — MegaSquirt / rusEFI (*.mlg)` | `*.mlg` |
| `TunerStudio text log (*.msl)` | `*.msl` |
| `MaxxECU log (*.MaxxECU-Zip-log)` | `*.MaxxECU-Zip-log` |
| `Delimited text — CSV / TSV (*.csv;*.tsv;*.txt;*.log)` | `*.csv;*.tsv;*.txt;*.log` |
| `All files` | `*.*` |

- **Reader selection:** each reader is tried in turn, and the first that says it can read the file reads it.
- **Errors.** A missing file fails with `Log file not found.`. A file no reader takes fails with `'{file name}' is not a recognised datalog format.`. A reader's own format, IO and access errors are reported the same way, in a dialog:
  - Title: `OpenLogViewer`
  - Button: OK
  - Icon: Warning
  - Text: `Could not open {file name}.\n\n{error message}`

  The current log stays on screen.
- **On success**, in order:
  1. Rebuild `Channels`. The file's channels come first, then the saved math channels evaluated over the new log.
  2. Apply pinned styles.
  3. Tick the default channels (1.4).
  4. Set `Title` = `{file name} — OpenLogViewer` and `Status` = the parts below joined with three spaces, `•`, three spaces:
     - `{format name}`
     - `{channels} channels`
     - `{samples:N0} samples`
     - `{duration:F1} s`
     - `{markers} markers`, only if there are any
     - `{signature}`, only if present
  5. Reseed the histogram axes, the axis sources and the filters, and clear the last search.
  6. Redraw.
- **Recent files:** there is **no** recent-files list in the C#. Nothing about opened logs is remembered between runs.

#### 1.8 Recording

- **Start** (Record button, or Tools → `● Record…`). The action exists only when `CanRecord`.
  - A save dialog opens: title `Record the live session to`, filter `CSV log|*.csv|All files|*.*`, default extension `.csv`, asks before overwriting.
  - The suggested name is `{ecu}-{yyyy-MM-dd_HH-mm-ss}.csv`. `{ecu}` is the live signature with invalid filename characters and spaces replaced by `-`, then trimmed of `-`. If that leaves nothing, or there is no signature, `live` is used.
  - The suggested folder is the remembered recording folder if it exists, otherwise `{workspace}/Logs` (created if needed).
  - Cancel does nothing.
- **`StartRecording(path)`**:
  - Not connected: returns `Not connected.`
  - Success: returns `Recording to {path}.`, and remembers that file's folder as the recording folder.
  - IO, access or argument errors: returns `Could not start recording: {error}`.
  - If a recording is already running, it is closed (complete) and replaced. The UI only reaches this from the Stop state, though.
- **Stop** (same button, labelled `■ Stop recording`). There is no dialog.
  - Not connected: `Not connected.`
  - Nothing being recorded: `Nothing was being recorded.`
  - Success: `Saved {rows:N0} rows to {path}. The session is still live.`
- **Record on connect.** Tools → `Record as soon as I _connect` is a check item, `RecordOnConnect`, persisted, **default off**. When it is on, each connect records to `{workspace}/Logs/live-{yyyy-MM-dd_HH-mm-ss}.csv`.
- **`RecordingSummary`:**
  - no session: ""
  - session not recording: `Saved {file name}` if it recorded earlier in this session, otherwise `Not recording`
  - recording: `● Recording {file name} · {rows:N0} rows · {seconds:F0} s`
- Every row is flushed as it is written, so a stopped recording is complete on disk.
- Tools → `Open the _recordings folder` opens `{workspace}/Logs` (created if needed) in the file manager.

#### 1.9 What is remembered between runs, and where

Settings files live in `%APPDATA%\OpenLogViewer\` (the per-user roaming application-data folder), as JSON.

| File | Contents |
|---|---|
| `settings.json` | theme id; interface zoom (`UiScale`, snapped to the allowed steps); data folder (workspace root, null = default); logging rate (Hz); single request block (bool); record on connect (bool, default false); last recording folder; known ECUs (port → remembered signature); ECU last-used times (port → timestamp); unit system (`AsReported` default / `Metric` / `Imperial`); OBD2 batch-failure counts |
| `presets.json` | channel presets: name and list of channel names |
| `filters.json` | data filters (channel, comparison, low, high, enabled) |
| `math.json` | calculated channels (name, units, expression) |
| `channels.json` | per-channel pinned colour (RGB int), pinned range (min/max, in the log's own units) and smoothing level, keyed by channel name. Capacity is limited, and a full store produces the "no room left" hints. |

The **workspace** defaults to `%USERPROFILE%\OpenLogViewer`, or the chosen data folder. It holds:
- `Logs\` (recordings)
- `Exports\`
- `ECU definitions\` (with `PUT ECU DEFINITION FILES HERE.txt`)
- `AgentStaging\` (agent-staged `.msq` and `.csv` files)
- `Projects\{vehicle}\project.json` and `Projects\{vehicle}\tunes\v{n}.msq`
- `agent-api.json` (only while the API runs)

**Not remembered:** window size and position, the open log, the mode, the view, lane layout, sort, `HideUnused` (always starts true), the search text, the agent write arming (never persisted, by design), the open project, recent files.

`--theme` and `--zoom` change the theme or zoom for one run without saving it.

---

### 2. The agent / MCP bridge

#### 2.1 Starting and stopping

- **It is on by default.** Exactly one place starts it at launch: `StartAgentApi(port)`. The port comes from `--agent-api {port}` when the value parses as an integer > 0, otherwise **8765**. `--agent-api` only picks the port; it does not decide whether the API starts. There is deliberately **no** command-line flag that arms writes.
- The server listens on `http://127.0.0.1:{port}/` (loopback only). Its address is `http://127.0.0.1:{port}`.
- On each start a new 32-byte random token is made (64 lowercase hex characters). This file is then written (overwriting any old one):

  `{workspace root}/agent-api.json`
  ```json
  {
    "address": "http://127.0.0.1:{port}",
    "websocket": "ws://127.0.0.1:{port}/live/stream",
    "token": "{token}"
  }
  ```
- **Start results** (returned as text and shown in `Hint`):
  - already running: `Already listening on {address}.`
  - success: `The agent API is listening on {address}. Its token is in {path to agent-api.json}.`
  - failure (port in use, IO or access): `The agent API could not start: {error} Another program may already have that port.` The API then counts as off.
- **Stop:**
  1. Close the socket and every live subscriber.
  2. Set `AgentWritesArmed = false`.
  3. Clear the activity indicator: `AgentIsActive` = false, `AgentLastAction` = "", `AgentIsWritingToEcu` = false, and cancel both decay timers.
  4. Delete `agent-api.json`, ignoring errors.

  The window then shows `The agent API is off and the socket is closed.`
- **Toggle:** clicking the toolbar toggle button or Tools → `_Agent API…` stops the API if it is running, otherwise starts it on the default port 8765. The toggle does **not** reuse the `--agent-api` port. There is no confirmation dialog either way.

#### 2.2 Where it shows in the UI

**Toolbar cluster.** This sits at the right end of the menu-bar row, right-aligned, 10 px right margin, vertically centred. Its tooltip (the whole cluster) is `AgentSummary`. Left to right:

1. **Light.** An 8×8 circle, 6 px right margin. Fill:
   - `Faint` when the API is off
   - `Accent` while `AgentIsRunning`
   - `Marker` while `AgentIsWritingToEcu` (this wins)
2. **Toggle button.** Padding 8,2. Label `AgentToggleLabel`: `AI API: On` while running, `AI API: Off` when not. Tooltip: `A local API an AI assistant or a script can watch this session through. Click to turn off; click again to turn back on`
3. **Write-arming toggle.** A toolbar-style toggle labelled `Allow AI writes`. Left margin 6, padding 8,2. Checked = `AgentWritesArmed` (two-way). **Enabled only while `AgentIsRunning`.** Tooltip: `Lets an agent change settings in the ECU's working memory. It never burns, and this clears itself on disconnect`

**Tools menu** (after `Tuning _project…` and a separator):
- `_Agent API…`. Tooltip `A local API an AI assistant or a script can watch this session through`. It toggles, as above.
- `Allow AI _writes`. A check item bound to `AgentWritesArmed`, enabled only while `AgentIsRunning`. Same tooltip as the toolbar toggle.

**Status-bar activity indicator.** It is in the status bar, docked right, 14 px left margin, and shown only while `AgentIsRunning`. Its tooltip is `AgentLastAction`.
- A `●` glyph: font size 11, 5 px right margin, resting opacity 0.35. Colour `Accent`, or `Marker` while `AgentIsWritingToEcu`. While `AgentIsActive` it pulses: opacity 0.35 → 1, 0.6 s, back and forth, repeating. When `AgentIsActive` goes false the pulse stops and the opacity returns to 0.35.
- Beside it, the `AgentLastAction` text: font size 11, max 220 px wide, cut off with an ellipsis. Colour `Muted`, or `Marker` while writing.
- This indicator is separate from `Hint` on purpose. Agent activity must never overwrite the person's own last-action message.

**`AgentSummary`:**
- off: `Off. No socket is open.`
- on: `Listening on {address} — {subscribers} watching, writes armed.` or `Listening on {address} — {subscribers} watching, read-only.`

`{subscribers}` is the number of open live-stream websockets.

**Activity state.** Every HTTP request and every open websocket is an "activity event" carrying a phrase.
- When an event arrives (on the UI thread): `AgentLastAction` = the phrase, `AgentIsActive` = true, and a 1.5 s one-shot timer is restarted that sets `AgentIsActive` back to false.
- If the event is a **write**-kind route, `AgentIsWritingToEcu` = true, with its own 1.5 s timer.
- A websocket counts as active for as long as it is open.

Route phrases:

| Route | Kind | Phrase |
|---|---|---|
| `/` | read | `asking what this API offers` |
| `/state` | read | `checking connection state` |
| `/channels` | read | `listing channels` |
| `/values` | read | `reading channel history` |
| `/insights` | read | `reviewing the log` |
| `/tune` | read | `reading the tune` |
| `/tables` | read | `listing tables` |
| `/table` | read | `reading a table` |
| `/tune/full` | read | `reading the whole tune` |
| `/log/full` | read | `reading the whole log` |
| `/context` | read | `catching up on this vehicle` |
| `/wire/health` | read | `checking the wire trace's health` |
| `/wire/events` | read | `reading the wire trace` |
| `/live/stream` | read | `watching the live stream` |
| `/project` | read | `reading the tuning project` |
| `/project/versions/compare` | read | `comparing tune versions` |
| `/project/record` | read | `recording a sitting` |
| `/project/keep` | read | `keeping this tune version` |
| `/project/fix` | read | `noting a fix` |
| `/tune/propose` | read | `working out what a change would do` |
| `/tune/set` | **write** | `writing to the ECU` |
| `/table/set` | **write** | `writing to the ECU` |
| `/tune/apply` | **write** | `applying a batch of changes to the ECU` |
| `/stage` | read | `listing staged files` |
| `/stage/tune` | stage | `staging a tune file` |
| `/stage/table` | stage | `staging a table as CSV` |
| `/definitions/needed` | read | `checking which firmware definition is missing` |
| `/definitions/import` | stage | `installing a firmware definition` |
| any other route | read | the route text itself |

**Open question:** the write light and text turn `Marker` as soon as a write-route request *arrives*, even when it is then refused (for example because writes are not armed). Nothing reaches the ECU in that case. Should the light mean "attempted" or "landed"?

#### 2.3 Read-only by default, and arming

- `AgentWritesArmed` is false at startup. It is never saved. It is set back to false on **every disconnect** (1.2) and when the API **stops**.
- The person arms it only through the `Allow AI writes` toolbar toggle or the `Allow AI _writes` menu check. Both are disabled while the API is off. No dialog asks for confirmation.
- When it changes, `Hint` is set to:
  - on: `An agent may now change settings and table cells in the ECU's working memory. Nothing it does is burned, and this turns itself off when you disconnect.`
  - off: `Agent writes are off. The API can still read everything.`
- **Open question / safety:** arming is possible with no ECU connected, and connecting does not clear it. Only a disconnect clears it. So someone who arms first and then connects to a car is armed on that car without being asked again. See Safety rules.

#### 2.4 HTTP protocol (what the Rust bridge must serve)

- **Authorisation** is checked on every request, websocket included. The request is accepted with `Authorization: Bearer {token}` (the scheme name is case-insensitive) or `?token={token}`. Tokens are compared in constant time. Failure gives **401** `{"error":"unauthorised","detail":"Pass the token as an Authorization: Bearer header, or ?token="}`.
- **Every error** is a JSON body `{"error": reason, "detail": detail}` with a status code:
  - 400: `no channel named` / `pass ?channel=`; `no table named` / `pass ?name=`; `the body was empty`; `the body was not the JSON this expects` / {parser message}
  - 404: `no such endpoint` / {path}; `no such table` / {name}; `no such stream` / {path}
  - 405: `that endpoint wants a POST` / {method}. Only endpoints that read a body check the method.
  - 409: every refusal from the view model (2.6)
  - 500: `the application failed to answer` / {error}
- The path has its trailing `/` trimmed, and an empty path is `/`.
- **Query parsing:**
  - `seconds`: an invariant-culture float, clamped to ≥ 0. A missing or bad value means 0, which means "all".
  - `raw`: true when present-and-empty, `true`, `1` or `yes`.
  - `count`: an int clamped to 0..500, default 100.
  - `decimate`: an int > 1, otherwise 1.
- POST bodies are JSON. Field names are camelCase as listed below.
- `GET /` returns `{application:"OpenLogViewer", api:1, endpoints:[…list…], limits:{…}}`.
- **WebSocket `/live/stream`.** At most 8 clients; the 9th gets **503**.
  - The client may send `{"channels":[…names…],"raw":false}`. An empty list or `"*"` means every channel. `raw:true` switches to the full set of decoded channels.
  - The server sends `{"type":"schema","channels":[…]}` when the channel set changes, then `{"type":"frame","t":seconds,"v":[values],"skipped":n}` at the ECU's pace. A missing value is NaN. Frames that were dropped are counted in `skipped`.
  - If the client sends something unparsable, the server answers `{"type":"error","detail":"expected {\"channels\":[…],\"raw\":false}"}`.
  - The live session feeds this stream from its own poll, not from the UI refresh. Every kind of live session must be wired to it.

#### 2.5 Every command the bridge exposes

The MCP translator (`openlogviewer-mcp`, stdio JSON-RPC, protocol `2024-11-05`, server name `openlogviewer`, version `1`) offers these tools. Each one forwards to the HTTP route shown. The **Gate** column says what must hold for it to succeed. "Arm" means the full write gate in 2.6.

| MCP tool | HTTP | Reads / does | Changes anything? | Gate |
|---|---|---|---|---|
| `olv_state` | GET /state | `{mode:"live"\|"log"\|"idle", signature, file (file name only), samples, seconds (last sample's time), rate (the configured logging rate while live, else 0), channels, hasTune (ECU tune in hand and not a placeholder), writesArmed, error:""}` | no | token |
| — | GET /limits | `{writeRateCount:10, writeRateWindowSeconds:5, maxChangeFractionOfRange:0.5, burns:false}` | no | token |
| `olv_channels` (`raw`) | GET /channels?raw= | `[{name, units, digits, role}]`. `role` is the channel-role name (EngineSpeed, Mixture, ManifoldPressure, …) for the first channel matching each role, else "". With `raw` on a live session that supports it: every decoded channel, with digits 2 and no role. No document: `[]`. | no | token |
| `olv_wire_health` | GET /wire/health | `{sampled, failures, successRate, failuresByKind, sinceLastSuccessSeconds, longestRecentMs}` | no | token |
| `olv_wire_events` (`count`) | GET /wire/events | the newest N wire events: sequence, time, context, origin, attempt, elapsed ms, outcome, failure kind, detail | no | token |
| `olv_values` (`channel`*, `seconds`) | GET /values | `{channel, count, times, values}`. The last `seconds` of samples (all when 0). An unknown channel or no log gives empty lists. | no | token |
| `olv_insights` | GET /insights | `[{level, topic, title, detail, evidence}]` from the log-insights analysis of the document | no | token |
| `olv_tune` | GET /tune | `{name: value}` for every non-array setting. Empty when there is no ECU tune or it is a placeholder. | no | token |
| `olv_tables` | GET /tables | the names of the ECU tune's tables | no | token |
| `olv_table` (`name`*) | GET /table?name= | `{name, units, columns, rows, xBins, yBins, xUnits, yUnits, xConstant, yConstant, values[row][col]}`. Name matching is case-insensitive. | no | token |
| `olv_tune_full` | GET /tune/full | every setting `{name, value, units, options, low, high}` plus every table, flattened as above | no | token |
| `olv_log_full` (`seconds`, `decimate`) | GET /log/full | `{times, channels:[{name, units, values}]}`, the tail of each, keeping 1 sample in every `decimate` | no | token |
| `olv_context` | GET /context | `{projectBrief, state, tune:{settingCount, tableNames}, insights, wireHealth}` | no | token |
| `olv_project` | GET /project | `{open: brief non-empty, vehicles:[names], brief}` | no | token |
| `olv_compare_versions` (`from`*, `to`*) | POST /project/versions/compare | `{from, to, changed: text}` (see 3.5) | no | token |
| `olv_record_sitting` (`note`) | POST /project/record | records the log as a sitting → `{recorded:true, brief}` | project file only | project open, log open |
| `olv_keep_tune` (`note`) | POST /project/keep | keeps the tune as a version → `{kept:true, brief}` | project file + `.msq` | project open, real tune |
| `olv_note_fix` (`id`, `title`, `detail`, `state`, `change`) | POST /project/fix | adds a fix or moves one along → `{noted:true, brief}` | project file only | project open; **no arming** |
| `olv_set_setting` (`name`*, `value`*, `rationale`*, `confirmDangerous`) | POST /tune/set | writes one setting to the ECU's working memory → `{written, value, burned:false, rationale}` | **ECU RAM** | **Arm** + per-setting guards |
| `olv_set_table_cell` (`table`*, `column`*, `row`*, `value`*, `rationale`*, `confirmDangerous`) | POST /table/set | writes one cell → `{written, column, row, value (as sent, after the clamp to the declared range), requested, clamped (abs diff > 1e-9), burned:false, rationale, warning}` | **ECU RAM** | **Arm** + per-cell guards |
| `olv_propose_tune` (`settings`, `cells`) | POST /tune/propose | applies the batch to a private copy of the tune → `{changes:[…], rejected:[…], summary}` | no | a real (non-placeholder) tune; **works unarmed and on a file tune** |
| `olv_apply_tune` (`settings`, `cells`, `note`*, `confirmDangerous`) | POST /tune/apply | writes the batch item by item through the same calls as set_setting and set_table_cell → `{applied, rejected, summary, burned:false}` | **ECU RAM**, then keeps a version | **Arm** once for the batch; per-item guards |
| `olv_stage_tune` (`settings`, `cells`, `filename`) | POST /stage/tune | writes a `.msq` (the tune as it stands, or with the batch applied) into `{workspace}/AgentStaging` → `{name, path, size, written}` | file only | a real tune; no arming |
| `olv_stage_table` (`name`*, `filename`) | POST /stage/table?name= | writes one table as an import-ready CSV into staging | file only | the table exists; no arming |
| `olv_list_staged` | GET /stage | `{files:[{name, path, size, written}]}` | no | token |
| `olv_definition_needed` | GET /definitions/needed | `{needed:false}`, or `{needed:true, need:{identity, family, signature, version, filename, nearby:[{name, signature, path}], folder, where, sources, guidance}}` | no | token |
| `olv_import_definition` (`path` or `content`, `source`, `name`) | POST /definitions/import | validates the `.ini` and installs it into the definitions folder → `{kept:true, name, signature, path, source}` | installs an `.ini` | it must declare the signature the ECU last reported (any signature when nothing has been connected) |

`*` = required. `settings` items are `{name, value}`. `cells` items are `{table, column, row, value}`. Columns and rows count from 0. The exact MCP tool descriptions are in `tools/OpenLogViewer.Mcp/Program.cs` and should be copied verbatim. For example, `olv_set_setting`'s description includes `Refused unless the person at the machine has ticked "Allow agent writes", …`.

**MCP-level failures.** These come back as tool results with `isError:true`:
- the token file is missing: `No agent API is running: {path} is not there. Start OpenLogViewer and turn on Tools → Agent API.`
- unreadable: `Could not read {path}: {error}`
- incomplete: `{path} does not name an address and a token.`
- the HTTP call failed: `OpenLogViewer did not answer: {error}. Is it running with the agent API started? It is under Tools, "Agent API".`
- an unknown tool: `no such tool: {name}`
- an unknown JSON-RPC method: error −32601 `no such method: {method}`

Any non-2xx HTTP answer is passed back as an error result with the body unchanged. The MCP reads `%USERPROFILE%\OpenLogViewer\agent-api.json`, or the path given as its first argument, **once**, and caches the address and token for the life of the process.

#### 2.6 Guardrails and refusals (exact text, `reason` / `detail`)

**Per-write order for `/tune/set` and `/table/set`.** Checks run in this order, and the first failure is returned:

1. **Rationale blank** (empty or whitespace): `no rationale given` / `Say in one line why this change is being made. It is echoed back with the write.` For apply: `no rationale given` / `Say in one line why this change is being made. It doubles as the version note.`
2. **General gate** (`GeneralRefusal`), in this order:
   1. not armed: `writes are not armed` / `Tick "Allow agent writes" in the application. It clears itself on disconnect.`
   2. no ECU connection and no MaxxECU USB source: `not connected to an ECU`
   3. placeholder tune: `the tune in hand is a placeholder` / `It came from a definition file rather than off the controller, and is all noughts.`
   4. tune from file: `the tune in hand came from a file` / `Read the ECU's own tune before writing to it.`
   5. engine above idle. On a running live session whose latest engine-speed reading is finite and **> 1200 rpm**: `the engine is running above idle` / `RPM reads {rpm:0}, above the 1200 rpm guard. Writes are refused while the engine may be under load or being driven; try again at idle or with it off.` RPM is read from the live session, never from the open document. If RPM cannot be found, the write is allowed.
   6. rate limit. When ≥ 10 agent writes have landed in the last 5 s: `too many writes in a short time` / `{n} agent writes landed in the last 5 seconds. Slow down, and check what each one did before sending the next.`
3. **No tune** (setting): `no tune has been read`
4. **Unknown setting:** `no such setting` / `This firmware declares no "{name}".` Unknown table: `no such table` / `{name}` (case-insensitive name match). Cell out of range: `that cell is not in the table` / `"{table}" is {columns} by {rows}.`
5. **Dangerous constant.** The setting's own name, or the constant behind the table, matches a dangerous-constant role (rev limiter, launch RPM, boost limit, fuel/ignition cut) and `confirmDangerous` is not true: `this setting needs confirmDangerous:true` / `"{constant}" matches the {role} guard — a setting whose job is to stop the engine hurting itself. Pass confirmDangerous:true if this change is intentional.`
6. **Value will not go in** (setting outside the declared range, or not numeric): `the value would not go in` / `It is out of the range the firmware declares, or the setting is not a number.`
7. **Magnitude.** |new − old| > 0.5 × (high − low) of the declared range: `that change is too large for one write` / `Moving this by {delta:0.###} is more than half of its declared {low:0.###}–{high:0.###} range in a single call. Make the change in smaller steps, checking what happened after each one.` This is skipped when the old value is NaN or the range is not finite or is ≤ 0. A refused setting is taken back out of the pending edit.
8. **Table cannot be edited:** `that table cannot be edited`
9. **The write itself** (see Safety). It succeeds only if the write path's message starts with `Sent` (case-insensitive). Otherwise: `the write did not go through` / {the write path's message}. For a table write, any text starting at ` Warning:` in the success message is split out into `warning`.

A write that lands is counted for the rate limit. A cell write also **selects that table** in Calibration, exactly as clicking it would. **Nothing is ever burned through the API.** Every write answer carries `burned:false`.

**Batch apply:**
1. Check the rationale, then the general gate **once**.
2. Lay the batch over a copy of the tune. If nothing would change, the answer is `Nothing valid to apply. {rejections}` or `Nothing would change; the ECU already holds these values.`
3. Otherwise, the whole batch counts as **one** write for the rate limit, and the per-item rate check is off for its duration. Each item then goes through the single-write path, so the RPM, dangerous and magnitude checks run on every item. Item refusals are collected as `{name}: {reason}` or `{table}[{c},{r}]: {reason}`.
4. What actually landed is measured by comparing the tune afterwards with the tune before.
5. If anything landed, the tune is kept as a project version with the note `{note} — applied: {d1}; {d2}…`. This is best effort, and a failure here never fails the apply.
6. The summary is `Applied {n:N0} change{s}.` plus any rejections, or `Nothing was applied. {rejections}`.

**Proposal rejections:**
- `a setting with no name`
- `{name}: no such setting`
- `{name}: {value} would not go in — out of range, or not a number`
- `{table}: no such table`
- `{table}[{c},{r}]: not in the table ({cols} by {rows})`
- `{table}: this table cannot be edited for this firmware`
- `{table}: could not be read off the working copy`

Summary: `Nothing would change.` or `{n:N0} setting{s} would change.`, followed by the rejections joined by spaces. With no tune or a placeholder: changes `[]`, rejected `["no tune has been read"]`, summary `No tune has been read, so there is nothing to propose changes against.`

**Staging:**
- No real tune: `no tune has been read` / `There is nothing to stage. Connect to an ECU and read its tune, or open a saved one.`
- Default names: `tune-{yyyy-MM-dd_HH-mm-ss}.msq`, and `{table name with spaces→'-' and invalid chars→'_'}-{yyyy-MM-dd_HH-mm-ss}.csv`.
- The `.msq` comment is `Staged by an agent.`
- A bad name: `could not stage that file` / {error}. An IO error: `could not write the staged file` / {error}.

**Project refusals:**
- `no project is open`, and for record also `Open one for this vehicle first, so there is somewhere to record it.`
- `there is no log to record`
- `the sitting was not recorded` / {message}
- `the tune was not kept` / {message}
- `no such state` / `Use open, applied, verified or abandoned.`
- `no fix by that name` / {id}
- `a new fix needs a title`
- `the project could not be saved` / {message}

**Definition import:**
- Not accepted: 409 `that definition was not kept` / {problem}.
- Accepted: `Hint` is set as in 1.6.
- `guidance` text, by case:
  - sources exist: `Fetch the first of these addresses that answers, check it declares "{signature}", and hand the file over with olv_import_definition — by path, having saved it, rather than by pasting its contents. It will be checked again before it is kept.`
  - MegaSquirt: `Do not fetch this one. MegaSquirt's licence restricts the definition itself and scopes redistribution to media accompanying a hardware sale, and msextra.com defends its downloads against automation. Look for it where it already is on this machine, and otherwise tell the person which firmware package and which variant file they need.`
  - otherwise: `There is no address to fetch this from. Ask the person where their definition came from, or look for one already on this machine.`

Every request handled by the server is tagged as agent-originated in the wire trace, so the Wire Trace window can tell agent traffic apart from the person's.

#### 2.7 The "Overview" feature

**It is not present on the source branch (`retro-theme`).** It exists only on the branches `virtual-dyno` and `wifi-obd2-and-batching` (commits `02a7b97`, "Let an agent publish an overview…", and `e768099`, "Add an "Ask AI to analyze" button…"). On those branches it is built on a different, in-process MCP server whose tools are not the `olv_*` set: `push_overview_report`, `get_overview_report`, `get_overview_selections`, `clear_overview`, and it refers to `edit_table`/`set_setting`. **Open question (user decision):** should the Rust rewrite include it? For reference only, this is what that branch does:

- **Entry.** A menu item `_Overview…` and a toolbar button `✦  Overview`. Both have the tooltip `What a connected AI agent found in the open tune and log, with the changes it proposes`. They open a single window titled `Overview` (880×720, centred on the owner), or bring the existing one forward and un-minimise it. Publishing a report also opens it.
- **Layout:**
  - Top band (`Panel` background, 1 px `Line` bottom border, padding 16,12):
    - the headline, at 16 SemiBold
    - `Revision {n}` right-aligned at 10 `Muted`, shown when a report exists
    - the summary, at 12, line height 17
  - Middle: either the empty message, or a scrolling list of findings. The empty message is `No AI overview has been generated yet. Ask your connected AI agent to review the open tune and log.`, 13 `Muted`, centred, margin 40.
  - Each finding is a card: `PanelAlt` background, radius 4, padding 12,10, a 3 px left border in the level colour. On the card:
    - a badge (`WARNING`/`WATCH`/`NOTE`/`GOOD`/`NOT MEASURED`, 10 bold, level colour: Warning→`Danger`, Watch→`Warning`, Note→`Accent`, Good→`Nominal`, else `Muted`) followed by the topic (10 `Muted`)
    - the title, 13 SemiBold
    - the detail, 12
    - the evidence, 10 monospace `Muted`, when present
    - when the finding carries a change, a checkbox whose label is `{table}[{col},{row}]  {current} → {proposed}` (table cell) or `{page} · {field}  {current} → {proposed}` (setting)
  - Bottom band, left to right:
    - `Ask AI to analyze`. Tooltip `Puts an analysis request on the clipboard, ready to paste into your connected AI agent's chat`
    - `Copy selected`. Tooltip `Puts the accepted changes on the clipboard, ready to paste back to the agent`
    - `Clear`. Tooltip `Discards this overview`
    - right-aligned tally, 11 `Muted`:
      - no changes and no notes: ""
      - no changes: `{k} note{s}`
      - otherwise: `{a} of {c} change{s} selected`, then ` · {k} note{s}` if there are notes
- **Ask AI to analyze** copies this exact prompt: `Using the OpenLogViewer MCP tools, fully analyze whatever tune and/or log is currently open: review the tune's key settings, check VE/ignition/AFR trends in the log, scan for faults, and note anything that looks off. Give concrete tuning suggestions. When you have findings, call push_overview_report so I can review and accept the ones I want.` The run log then gets `Analysis request copied — paste it into your connected AI agent's chat.` MCP cannot start an agent turn from the app, so copying a prompt is the whole action.
- **Copy selected:**
  - none ticked: `Nothing is ticked — nothing to copy.`
  - otherwise it copies `Apply the following accepted changes, then publish the next revision:` + blank line + one `- {title}: {change description}` per ticked finding, and reports `{n} change{s} copied.`
  - the clipboard is busy: `The clipboard was busy — nothing was copied.`
- **Clear** empties the findings, headline and summary, and sets the revision to 0.
- **Publishing** replaces the whole report (it does not merge) and increments the revision. A bad level is refused with `'{level}' is not a level. Use Warning, Watch, Note or Good.` A change missing what it needs is kept as a plain note, with the warning `'{title}': change kind '{kind}' was missing what it needs, so it was kept as an observation with nothing to apply.` The `get_*` tools with no report answer `No overview has been published. Call push_overview_report.`
- **Nothing in Overview reaches the tune.** Ticking only records a choice. Applying is still the agent's ordinary write tools, under the ordinary gates.
- **Open question:** on that branch the window treats "a report exists" as "has at least one finding". A report with a headline and no findings shows the "No AI overview…" message.

---

### 3. The Tuning project window

#### 3.1 Opening it

- From Tools → `Tuning _project…` (tooltip `What is wrong with this tune, what has been tried, and whether it worked`), or the toolbar button `⌸  Project`. The button is right-docked, padding 10,3, right margin 8, minimum height equal to the Connect button's height. Its tooltip is `What is wrong with this tune, what you changed, whether it worked — and which tune each log came from`. It is **always enabled**, with or without a log.
- One window per main window. It is not modal: the main window owns it and it is centred on the owner. If it is already open, it is brought to the front. Its content is not rebuilt then, and the picker is not refilled. Closing it forgets it.
- **`--tuning-project [vehicle]`.** The next argument is taken as a vehicle name unless it starts with `--`. With a name, the app first calls `OpenProject(name)` (opening or creating that project) and sends the result to `Hint`. Either way it opens the window, then sends `project window opened: {ProjectSummary}` to `Hint`.

#### 3.2 Layout

Window title `Tuning project`, 820 wide × 700 high, background `Bg`. Three parts:

**Top band.** `Panel` background, 1 px `Line` bottom border, padding 16,12. Stacked top to bottom:

1. The caption `Which vehicle`. Size 11, `Muted`, 6 px bottom margin.
2. Row 1:
   - left: the **vehicle picker** (drop-down, 300 wide, tooltip `Projects on this machine`, not editable), then the **`Open`** button (left margin 8, padding 14,4)
   - right: the **`Record this log`** button (padding 14,4, tooltip `Runs the insights over the log in hand and keeps what they found`)
3. Row 2 (top margin 6): the **new-vehicle box** (text box, 300 wide, padding 6,4, tooltip `A name for a car with no project yet`), then the **`Start a new one`** button (left margin 8, padding 14,4).
4. The **note box**: a full-width text box, top margin 8, padding 6,4, tooltip `What this sitting was — what you changed before it, what you are testing`. Beneath it, the caption `What changed before this log, if anything` (size 10, `Muted`, margin 2,3,0,0).
5. The **compare row** (top margin 10). It is **hidden unless the open project has two or more tune versions**. Left to right:
   - `What changed from` (11, `Muted`, 6 right margin)
   - **From** drop-down (90 wide)
   - `to` (11, `Muted`, 6 margin each side)
   - **To** drop-down (90 wide)
   - **`Compare`** button (left margin 8, padding 12,3, tooltip `Setting by setting, in the firmware's own units — not byte by byte`)
   - right-aligned: **`Back to the project`** (padding 12,3)
6. The **summary line**: `ProjectSummary`, size 11, `Text`, wraps, top margin 8.

**Bottom band.** `Panel` background, 1 px `Line` top border, padding 16,10.
- Left: `Copy` (padding 12,4, right margin 8, tooltip `Puts the whole project on the clipboard, which is what to paste to an assistant`), `Open the folder` (padding 12,4, right margin 8), `Keep this tune` (padding 12,4, tooltip `Records the tune now in hand as a version of this project, with the note above`).
- Right: the static text `Nothing here is sent anywhere. The file is yours.` (11, `Muted`).

**Body.** It fills the rest and scrolls vertically, padding 16,12. It is a read-only, borderless, transparent, wrapping, selectable text area in `DataFont` 12 `Text`: the **brief**.

#### 3.3 States and contents

- **On open** (`Reload`):
  1. Fill the picker with every vehicle that has a project. That is every folder under `{workspace}/Projects` that contains `project.json`, by folder name, sorted case-insensitively.
  2. Select the open project's vehicle if it is in the list, otherwise the first entry.
  3. If the new-vehicle box is empty and no project is open, prefill it with `LiveSignature` (empty when not connected).
  4. Show the project (below).
- **Show(project):**
  1. Refill both version drop-downs with version ids, newest first, and show the compare row only when there are ≥ 2. Default: **To** = newest (index 0), **From** = the one before it (index 1).
  2. Set the brief:
     - no project:

       ```
       No project open.

       A project keeps what the insights found on each log, and what you are trying to fix — what is wrong, what you changed, and whether it worked. That is the part no single log can tell you, and the part an assistant needs in order to be useful rather than start over every time.

       Name a vehicle above and press Open.
       ```
     - otherwise the project's Markdown brief with the 10 most recent sittings (3.7).
  3. Refresh the summary line.
- The window follows the main state. Whenever `Project` or `ProjectSummary` changes (including from an agent call), it runs Show again. That resets the compare view to the brief.
- **`ProjectSummary`:**
  - none: `No project open. One keeps track of what is wrong with this tune and what has been tried.`
  - otherwise: `{vehicle} — {n} sitting{s}, {k} open fix{es}.` ("fix" when k = 1, otherwise "fixes")
- No button is ever disabled. Each one checks its own preconditions when clicked.

#### 3.4 Controls and their actions

Every dialog below is a message box titled `OpenLogViewer` with a single OK button and the Information icon unless noted.

| Control | Action |
|---|---|
| **Open** | If nothing is picked: dialog `Pick a project first, or start a new one below.` Otherwise `OpenProject(picked)`, then Reload. |
| **Start a new one** | Trim the box. If it is empty: dialog `Give the vehicle a name first.` Otherwise `OpenProject(name)` (which opens the project if one with that name already exists), clear the box, Reload. |
| **Record this log** | No project: dialog `Open a project first, so there is somewhere to record it.` Otherwise `RecordSitting(trimmed note)`, clear the note box, Show, then a dialog with the returned message. |
| **Keep this tune** | No project: dialog `Open a project first.` Otherwise `KeepTune(trimmed note)` (the note box is **not** cleared), Show, then a dialog with the returned message. |
| **Compare** | If either drop-down is empty, do nothing. Otherwise replace the brief text with `CompareVersions(from, to)`. |
| **Back to the project** | Show the open project again (the brief returns). |
| **Copy** | Put the brief's current text (which may be a comparison) on the clipboard. If the clipboard is busy, ignore it silently. |
| **Open the folder** | Open the project's folder (`{workspace}/Projects/{safe vehicle}`), or the `Projects` root when no project is open, creating it first, in the system file manager. On failure: dialog with the error message, Warning icon. |

The Open and Start handlers **ignore the message `OpenProject` returns**, so an open failure is not shown (**Open question**).

**`OpenProject(vehicle)`:**
- Blank: `A project needs a name for the vehicle.`
- Otherwise read `Projects/{safe(vehicle)}/project.json`, or create a project with `Vehicle` = the trimmed name and `Signature` = `LiveSignature`. If connected and the stored signature differs from the live one, update it.
- Write it back (this creates it).
- Returns `Project open: {ProjectSummary}`, or on an IO or access error `That project could not be opened: {error}`.

`safe(vehicle)`: replace invalid filename characters with `-`, then trim whitespace and dots. If that leaves nothing, use `vehicle`.

`CloseProject()` exists but no control in this window calls it.

**`RecordSitting(note)`:**
- `No project is open.` / `There is no log to record.`
- Otherwise run the log insights over the document. Record a sitting against the latest version id, with the log name, samples, seconds, signature, note and findings. Raise a fix for each newly warned-about item, and add a repeat note to an existing fix that already tracks it. Save.
- Returns `Recorded {n} findings from {log}` + (`, raising {r} new fix{es}.` if r > 0, otherwise `.`) + ` {open} open in total.`
- A save error returns `The project could not be saved: {error}`.

**`KeepTune(note, burned=false)`:**
- `No project is open.`
- No ECU tune, or a placeholder: `There is no tune to keep. Connect to an ECU and read its tune, or open a saved one.`
- Otherwise fingerprint the tune: SHA-256 over all page bytes, first 16 hex characters, lowercase.
  - **Same fingerprint as an existing version:** update that version. It is marked burned if this keep is a burn, and gets this note if it had none. Returns `That is already {id} — the tune has not changed since it was kept.`
  - **New:** id `v{highest+1}`, file `tunes/{id}.msq` written with the comment = the note, or `{vehicle} {id}` if there is no note. Its parent is the previous newest version. Returns `Kept as {id}. {count} version{s} in this project.`
- A save error returns `The project could not be saved: {error}`.
- **Automatic keep on burn.** After a successful burn (see Safety), if a project is open and the tune is real, the app keeps the tune with the note `burned to flash` and burned = true, and sets `Hint` to the result if it starts with `Kept`. An exception sets `Hint` to `Burned, but the project could not record it: {error}`. It is silent when no project is open.

#### 3.5 Comparing versions

`CompareVersions(from, to)`:
- `No project is open.`
- No definition layout loaded: `No firmware definition is loaded to read them through.`
- A version is missing or its file is gone or unreadable: `One of {from} and {to} is not a version of this project, or its file has gone.`
- No differences: `{from} and {to} hold the same tune.`
- Otherwise: `{n:N0} setting{s} differ between {from} and {to}.`, then a newline, then one `- {difference summary}` line per difference, at most 200. If there are more, add `…and {k} more.`.

The same text is what agents get.

#### 3.6 Fix notes (agent only)

There is no UI for fixes in this window. They appear only in the brief. `olv_note_fix` works as follows:
- With an id it updates that fix. A non-empty title, detail or change replaces the old one. If the detail changed, `{yyyy-MM-dd}: {detail}` is appended to the fix's evidence list.
- Without an id it raises a new fix. The title is required. The id is a slug of the title (lowercase letters and digits, other runs → `-`).
- The state defaults to `open`.
- Moving to `verified` or `abandoned` stamps a settled time once. Moving back to open clears it.

#### 3.7 The brief (Markdown, shown verbatim as text)

```
# {vehicle}

**Engine** {engine}                (only if set)
**Firmware** {signature}           (only if set)
**Started** {yyyy-MM-dd}
**Sittings** {n}   **Open fixes** {k}

{notes}                            (only if set, preceded by a blank line)

## Still open

Nothing outstanding.               (or, per open fix, oldest raised first:)
### {id} — {title}

*{state lowercase}, raised {yyyy-MM-dd}*

{detail}
**Changed** {change}
**Since**
- {evidence line}

## Settled                         (only if any; newest settled first)
- **{id}** — {title} ({state})[. {change}]

## Tunes                           (only if any; newest first, at most 8)
- **{id}** {yyyy-MM-dd}[, burned][ — {note}][ (for {addresses})]
*{k} earlier tunes are in the file.*   (if more than 8)

## Recent sittings

None recorded.                     (or, per sitting, newest first, last N:)
### {yyyy-MM-dd HH:mm}[ — {log}][ · on {version}]
{samples:N0} samples over {seconds:N0} s[, {signature}].

{note}

Nothing flagged.                   (or one line per Warning/Watch finding:)
- **{level}** {topic} — {title}

*{k} earlier sittings are in the file.*   (if more than N)
```

Blank lines appear between the blocks as shown. N is 10 in this window and 5 everywhere else (the agent brief and `/context`).

---

### Open questions

1. **Overview is missing from the source branch** (2.7). It exists only on `virtual-dyno` / `wifi-obd2-and-batching`, on a different MCP architecture with different tool names. *User decision.*
2. On that branch an Overview report with zero findings shows the "No AI overview…" message, because "has a report" is taken to mean "has at least one finding". *Builder can settle (use "headline or findings present") if the feature is kept.*
3. `agent-api.json` is written under the **workspace root**, but the MCP translator defaults to `%USERPROFILE%\OpenLogViewer\agent-api.json`. They differ once the data folder has been moved. *Builder can settle (e.g. also write to the fixed path); user may prefer otherwise.*
4. The MCP translator caches the address and token for its whole life. After OpenLogViewer restarts (new token), every call fails with 401 until the MCP process restarts. *Builder can settle (re-read the file on 401).*
5. MCP error texts say `Tools → Agent API` / `"Agent API"`, the menu item is `_Agent API…`, the toolbar says `AI API: On/Off`, and the arming control is `Allow AI writes`. But the refusal text and the MCP tool descriptions say `Tick "Allow agent writes"`. *User decision on one wording; until then, copy each exactly.*
6. `AgentToggleLabel`'s code comment says it names "the action a click takes, not the current state", yet the text shows the current state (`AI API: On` while running). *Spec ambiguity; the C# text shows state, so build that.*
7. `Tools → _Agent API…` ends in an ellipsis but opens no dialog. It just toggles. *User decision.*
8. The write light and text (`Marker`) turn on when a write-route request arrives, even if it is refused before anything is sent. *User decision: "attempted" or "landed".*
9. `AgentSummary` (the cluster tooltip, including "{n} watching") is only refreshed when the API starts or stops, or arming changes. It is not refreshed when a websocket opens or closes, so the count can be stale. *Builder can settle (recompute it whenever it is read).*
10. **Arming survives a connect.** Arming is allowed with no ECU connected, and only a disconnect clears it. Arm → connect to a car = armed on that car. *User decision (likely: clear on connect too, or disable arming while not connected).*
11. `olv_state.rate` reports the configured logging rate, not the measured rate. *Spec ambiguity; build as the C# does unless told otherwise.*
12. Opening a saved log while live is overwritten by the next live tick (1.5). *User decision: refuse, disconnect, or pause live refresh.*
13. The `Record as soon as I connect` tooltip says `On by default.` but the setting defaults to **off**. *User decision on the text; default stays off.*
14. CSV export says "No BOM" in its comment but writes a UTF-8 BOM. *User decision; C# behaviour = BOM.*
15. The Project window's Open and Start a new one ignore `OpenProject`'s result, so `That project could not be opened: …` is never shown. *Builder can settle (show it in a dialog or `Hint`).*
16. The project picker lists **folder** names (made safe), while selecting the open project matches on the stored vehicle name. A name with invalid characters will not be re-selected. *Builder can settle (list stored names).*
17. Reactivating an already-open Project window does not refill the picker, so a project created by an agent meanwhile is missing from the list until the window is reopened. *Builder can settle.*
18. `--tuning-project` and `--agent-api` are treated as switches that take a value. When either is given without one, a log path right after it is swallowed and not opened. *Builder can settle.*
19. The toolbar toggle and menu always restart the API on port 8765, even if it was started with `--agent-api {port}`. *Builder can settle (remember the last port).*

### Safety rules found

1. **Agent writes start disarmed.** The flag is never persisted, there is no CLI flag that arms it, it is cleared on every disconnect and on API stop. It is armed only by the `Allow AI writes` toggle or the `Allow AI _writes` menu check, which are enabled only while the API runs. Hints: on `An agent may now change settings and table cells in the ECU's working memory. Nothing it does is burned, and this turns itself off when you disconnect.`; off `Agent writes are off. The API can still read everything.`
2. **Arming is not cleared by connecting** (Open question 10). Arming before connecting to a car leaves the car armed.
3. **The API never burns.** Every write answer has `burned:false`, and `/limits` says `burns:false`.
4. **General write gate**, in order: armed → connected (ECU connection or MaxxECU USB) → not a placeholder → not from a file → RPM ≤ 1200 (from the live session; allowed if unknown) → fewer than 10 landed writes in 5 s. Exact refusals are in 2.6.
5. **A rationale is required** on every write and batch (refused first).
6. **Dangerous constants** (rev limiter, launch RPM, boost limit, fuel/ignition cut) need `confirmDangerous:true`: `this setting needs confirmDangerous:true` / `"{constant}" matches the {role} guard — …`.
7. **Magnitude cap**: a single change may move at most 50 % of the declared range: `that change is too large for one write` / `Moving this by {delta:0.###} is more than half of its declared {low:0.###}–{high:0.###} range in a single call. …`. A refused setting is reverted from the pending edit.
8. **A batch counts as one write for the rate limit**, but every item still passes the RPM, dangerous and magnitude checks. What landed is measured, not inferred.
9. **Cell values are clamped** to the declared range. The answer reports `requested`, `value` and `clamped`, so a silent clamp is visible.
10. **Propose and stage never reach the ECU** and work unarmed. A placeholder tune is refused for them.
11. **Definition import** must match the signature the ECU last reported. A mismatch gives 409 `that definition was not kept`. The guidance forbids fetching MegaSquirt definitions.
12. **Token auth** on every request (constant-time compare). Loopback only. A new random token each start. The token file is deleted on stop.
13. **Project notes and fixes need no arming** (they cannot touch an engine). Keep-tune writes only project files.
14. **Every agent request is tagged agent-originated** in the wire trace.
15. The **write light and text turn `Marker`** for agent write requests (Open question 8), and the status-bar indicator shows only while the API runs.
16. The ECU write and burn gates the screens use (`CanWriteSettings`, `CanWriteTable`, `CanWriteCurve`, `CanBurn`, `CanBurnSettings`, `CanApplyRestore`) all require a connection and `NOT TuneIsPlaceholder AND NOT TuneIsFromFile`. The full write paths are covered under Safety.


## §8 `w8-viewmodel`: connect, write, burn, restore and every guard (safety-critical)

### Safety-critical behaviour of `olv::state` (connect, write, burn, restore, clear codes)

This section is the checklist for every path that connects to an ECU or vehicle, or changes one, and for every guard the interface applies on the way. It was written from `MainViewModel.cs`, `SavedTuneCommands.cs`, `AgentCommands.cs`, `MainWindow.xaml(.cs)`, `FaultsPanel.xaml.cs` and `App.xaml.cs` on branch `retro-theme`. Where it needed to say what a call does, it also read the Core helpers (`EcuConnection`, `LiveSession`, `TuneRestore`, `TuneSettingsEdit`, `TuneEdit`, `DangerousConstants`, `Obd2Faults`, `MsProtocol`).

Rules marked **(from fix-burn-after-reopen, not yet on main)** come from branch `fix-burn-after-reopen` (commits `aecc523`, `3c1896d`, diffed against `97778bb`). Build them as specified, because they close a real burn-commits-the-old-tune defect. They are kept separate so that whoever merges can tell them apart.

Conventions used below:

- **Hint** is the status strip's main text: the bottom bar, left part, font size 11, colour `Muted`, trimmed with an ellipsis, with the full text as its tooltip. It is "the result of the last thing the human did". Every write, burn and restore result string is shown there. The window's `Report(text)` sets Hint to `text`, and also appends `HH:mm:ss  {text}` to the application's run log.
- **Status** is the toolbar's flexible text: last in the toolbar row, colour `Muted`, trimmed, with itself as its tooltip.
- **LiveStatus** is the bottom bar's leftmost text: font size 11, max width 320, trimmed, tooltip `LiveDetail`, visible only while live. Its colour is `Accent`, or `Marker` while `LiveHealthy` is false.
- Every confirmation dialog in scope is a modal message box. The title is always `OpenLogViewer` and the icon is a warning. The buttons are **OK** and **Cancel**, with **Cancel the default**: Enter on an unmoved focus, Esc and the close box all mean Cancel. **Only OK proceeds.** The body text is given exactly; `\n` is a line break.
- Error dialogs are titled `OpenLogViewer`, have one **OK** button and a warning icon unless stated otherwise.
- Number formats are .NET format strings. `N0` means grouped digits (`1,234`), `F1` means one decimal place, `0.###` means up to three decimals, and `X2` means two upper-case hex digits.
- `{s}` means the plural suffix: empty when the count is exactly 1, otherwise `s`.
- Writes, burns, restores and fault clears all run **synchronously on the UI thread**. The window does not repaint until they return, no wait cursor is set, and no "in progress" text is shown (see Open questions).

---

### 1. Connection lifecycle

#### 1.1 State model

There are three independent state variables. A builder should model them separately and not as one enum.

**A. Link state** (`MainViewModel`: `_live`, `IsLive`, `LiveHealthy`)

| State | How it is recognised | What the user sees |
|---|---|---|
| **Disconnected** | no live session (`_live == null`), or the session's poll thread is not alive | Toolbar Connect button reads `Connect ▾`. LiveStatus is hidden. The Record button is hidden. The Reconnect button is visible only if `CanReconnect`. |
| **Probing** (window-side, cabled/Bluetooth serial menu connect and Wi-Fi connect only) | async probe in flight | Connect button **disabled**. Hint is `Connecting to {port.Label}…` (serial) or `Connecting to the Wi-Fi adapter at {address or "the usual addresses"}…` (Wi-Fi). The window stays responsive. |
| **Connecting** (view-model connect running on the UI thread) | inside `Connect*` | Wait cursor. The window is frozen: this includes the whole tune read, which takes about 3 s on a MegaSquirt over serial. |
| **Scanning paired devices** | "Check which of these are switched on" running | Connect button **disabled**, its text is `Scanning…`. About 3 s of BLE listening, plus a parallel open of each paired serial port. |
| **Connected – live** | `IsLive` (poll thread alive) and the session is not reconnecting | Connect button reads `Disconnect`. LiveStatus is `● {port} · {signature} · {rate:F1} Hz[ · {retries} retries] · {not recording | REC {rows:N0} rows}` in `Accent`. The Record button is visible. |
| **Reconnecting** (the session is trying to get a lost link back) | `LiveSessionStatus.Reconnecting` is true. `IsLive` **stays true**. | LiveStatus is `○ {port} · waiting for the ECU to come back…` in `Marker` (`LiveHealthy` = false). |
| **Faulted → Disconnected** | `LiveSessionStatus.Error` is non-null | On the next UI tick, LiveStatus briefly shows the error. Then `Disconnect()` runs and Hint is set to the error text (see 1.3). |
| **Disconnecting** | `Disconnect()` | Instant and synchronous: the session is stopped and disposed, which closes the port. |

The connection kind, fixed at connect time, decides what a session can do:

| Kind | Entry point | Write object held | Tune read at connect | Can Send (UI) | Can Burn | Can Restore | Fault codes |
|---|---|---|---|---|---|---|---|
| TunerStudio page-protocol ECU (MegaSquirt, Speeduino, rusEFI) over a cable or a Bluetooth serial port | `Connect(port, bluetooth)` | `_ecuConnection` | yes (`ReadTuneFromEcu`) | yes | yes | yes | no |
| MaxxECU over USB (FTDI D2XX) | `ConnectMaxxEcuUsb(serial)`, **command line only** | `_maxxUsbSource` (`_ecuConnection` stays null) | yes (`AdoptMaxxTune`) | **no on main**: buttons are disabled, see Open question Q1; the agent API can write | no burn exists | no | no |
| MaxxECU over Bluetooth serial | `ConnectMaxxEcu(port)` (a port recognised as MaxxECU by name) | none | no | no | no | no | no |
| OBD2 ELM327 (serial, BLE or Wi-Fi) | `ConnectObd2`, `ConnectObd2Ble`, `ConnectObd2Wifi` / `StartObd2Session` | `_obd2` | no | no | no | no | **yes** (`IsObd2Live`) |
| Subaru SSM | `ConnectSsm(port)` | none | no | no | no | no | no |
| Holley Sniper | `ProbeSniper()`, **command line only**. This is a diagnostic probe, not a session. | none | no | no | no | no | no |

**B. Tune provenance** (`_ecuTune`, `TuneIsPlaceholder`, `TuneIsFromFile`)

| State | Set by | Writes allowed |
|---|---|---|
| No tune (`_ecuTune == null`) | start-up; a failed `ReadTuneFromEcu` | no |
| Read from the controller (`TuneIsPlaceholder = false`, `TuneIsFromFile = false`) | `ReadTuneFromEcu` on a successful TunerStudio connect; `AdoptMaxxTune` on MaxxECU USB (which does **not** reset the two flags; see Q2) | yes, if connected |
| Placeholder (`TuneIsPlaceholder = true`): a definition file opened with no tune, so every value reads 0 | `OpenDefinition(iniPath)` | **never** |
| From a saved file (`TuneIsFromFile = true`) | `OpenSavedTune(path)` (Tools ▸ Open a saved tune…) | **never** (save and compare only) |

**C. Recording** (`IsRecording`) is independent of A. It is only possible while live (`CanRecord = IsLive`). It changes nothing on the ECU.

#### 1.2 What each state enables (exact gates)

These are the view-model properties the buttons and menu items depend on. Re-evaluate them every frame: they read `_ecuConnection`, which changes without notification (`RaiseWriteGates()` exists in the C# only because WPF needs telling).

| Gate | Rule (main) | Rule change (from fix-burn-after-reopen, not yet on main) |
|---|---|---|
| `CanWriteTable` | `HasTableChanges && _ecuConnection != null && _tuneLayout != null && !TuneIsPlaceholder && !TuneIsFromFile` | `(HasTableChanges \|\| HasLostWrites) && …rest unchanged` |
| `CanWriteSettings` | `HasSettingChanges && _ecuConnection != null && _tuneLayout != null && !TuneIsPlaceholder && !TuneIsFromFile` | `(HasSettingChanges \|\| HasLostWrites) && …` |
| `CanWriteCurve` | `HasCurveChanges && _ecuConnection != null && _tuneLayout != null && !TuneIsPlaceholder && !TuneIsFromFile` | `(HasCurveChanges \|\| HasLostWrites) && …` |
| `CanBurn` (table Burn button, and the **Restore** menu item) | `_ecuConnection != null && _tuneLayout != null && !TuneIsPlaceholder && !TuneIsFromFile`. It does **not** require that anything was sent. | unchanged |
| `CanBurnSettings` (settings Burn and curve Burn) | `_settingsPagesWritten.Count > 0 && _ecuConnection != null && !TuneIsPlaceholder && !TuneIsFromFile` | unchanged |
| `CanApplyRestore` | `PendingRestore != null && !PendingRestore.IsEmpty && _ecuConnection != null && !TuneIsPlaceholder && !TuneIsFromFile`. No control is bound to it. | unchanged |
| `CanSaveTune` | `_ecuTune != null && _tuneLayout != null && !TuneIsPlaceholder` | unchanged |
| `CanReconnect` | `!IsLive && LastConnected != null` | unchanged |
| `CanRecord` | `IsLive` | unchanged |
| `IsObd2Live` | `_obd2 != null && IsLive` | unchanged |
| Disconnect menu item | enabled when `IsLive` | unchanged |
| "Allow AI writes" toggle | enabled when `AgentIsRunning` | unchanged |

`_settingsPagesWritten` is the set of page indices written since the tune was read, for a burn to commit. Pages are **added** by: settings Send (each page written), curve Send, restore apply and, (from fix-burn-after-reopen, not yet on main), by re-sending lost bytes. A **table Send does not add its page.** Pages are **removed** by: a successful settings burn (per page, as it lands); a settings burn that is unconfirmed (no reply), which strikes the page off; a page with no burn command; a successful table burn of that page. The set is **cleared** by `ReadTuneFromEcu` (every TunerStudio connect), `OpenDefinition` and `OpenSavedTune`. It is **not** cleared by `Disconnect`, `AdoptMaxxTune` or the OBD2/SSM/MaxxECU-BT connects.

#### 1.3 Transitions

| From → To | Trigger | What happens |
|---|---|---|
| Disconnected → Probing → Connecting → Live | A serial or Bluetooth port item in the Connect menu (toolbar `Connect ▾` popup, or Tools ▸ Connect submenu) | 1. Connect button disabled; Hint `Connecting to {port.Label}…`. 2. On a background thread the port is opened and closed once (1 attempt for a cable, 2 for Bluetooth). 3. If that fails: the port is remembered as `no answer at HH:mm`, a dialog shows `Could not connect on {port.PortName}.\n\n{exception message}`, Hint becomes `{port.PortName} did not answer.`, and the state returns to Disconnected. 4. Otherwise the port is remembered as `answered at HH:mm` and the Connect button is re-enabled. Then, under a wait cursor, the dispatcher picks: forced OBD2 or a port recognised as OBD2 → OBD2 connect; a port recognised as MaxxECU → MaxxECU-BT connect; otherwise → TunerStudio connect (Bluetooth timings if the port is Bluetooth). 5. If that throws: dialog `Could not connect on {port}.\n\n{exception message}`. Hint is **left at** `Connecting to …` (Q9). 6. If it succeeds: the Connect button reads `Disconnect`, plot follow mode turns on, and a 200 ms UI refresh timer starts. |
| Disconnected → Live | "Connect as an OBD2 adapter ▸ {port}" | Same as the row above, with OBD2 forced. |
| Disconnected → Live | "Connect over SSM (Subaru) ▸ {port}" | No probe. `ConnectSsm(port)` runs straight away. On failure: dialog with the exception message alone. |
| Disconnected → Live | a BLE OBD2 adapter item | No probe. Wait cursor; `ConnectObd2Ble`. On failure: dialog `Could not connect to {adapter.Name}.\n\n{message}`. |
| Disconnected → Probing → Live | "Connect to a Wi-Fi OBD2 adapter ▸ {address}" | Connect button disabled; Hint `Connecting to the Wi-Fi adapter at {address}…`. The socket is opened on a background thread. On failure: every address tried is remembered as `no answer`, a dialog shows `Could not connect over Wi-Fi.\n\n{message}`, and Hint becomes `The Wi-Fi adapter did not answer.` If the open succeeds but the session cannot start: the source is disposed and a dialog shows `Could not start the session.\n\n{message}`. |
| Live → Disconnected → Live (**switching ECU**) | any connect item while live; Ctrl+K while live | Every `Connect*` begins with `Disconnect()`. **There is no confirmation.** On main nothing at all warns about unsent or unburned changes. The fix branch warns in Hint, but the new connection's Hint overwrites it at once (Q5). |
| Disconnected → Live (repeat the last connection) | toolbar Reconnect button (visible when `CanReconnect`, label `ReconnectLabel`), Tools ▸ `{ReconnectLabel}` (enabled when `CanReconnect`), **Ctrl+K** (always bound) | `ConnectTo(LastConnected.PortName, LastConnected.IsObd2)` with **quiet** errors: a failure gives **no dialog and no Hint**, only run-log lines (`connecting on {port}…`, `Could not connect on {port}: {exception}`, `not live`). See Q8. There is no probe step. Ctrl+K checks only `LastConnected`, not `IsLive`, so pressing it while live disconnects and reconnects. |
| Live → Disconnected | Tools ▸ Disconnect; the toolbar button while it reads `Disconnect`; window **Closed** | The window stops the refresh timer and calls `Disconnect()`. The toolbar button text goes back to `Connect ▾`. |
| Live → Reconnecting | 20 consecutive failed polls (`FailuresBeforeStopping`) | The session tries `source.Recover()` repeatedly. The first wait is 750 ms, each later wait doubles, up to 6 s. The window is `ReconnectFor`: 60 s, or **120 s for MaxxECU-BT**. For a TunerStudio ECU, `Recover` = **close and reopen the port** (which **resets a Speeduino**) and read one realtime block. (from fix-burn-after-reopen, not yet on main) It also waits `BootWait` (2,500 ms) between the reopen and the read. |
| Reconnecting → Live | `Recover` succeeds | The failure count resets. LiveStatus goes back to `●`. The agent write arming is **not** cleared. |
| Reconnecting → Faulted → Disconnected | the window expires | Error text: `The ECU did not come back within {ReconnectFor seconds:F0} seconds. The session so far is still here and its recording is complete.` (If `ReconnectFor` were 0: `The ECU stopped responding — switched off, or the cable pulled. The session so far is still here and its recording is complete.`) On the next 200 ms tick `RefreshLive` calls `Disconnect()` and then sets Hint to that error, so the error replaces any Disconnect Hint. |

Start-up never connects on its own: the application must never take a port unasked. The command-line flags in 2.5 are the only automatic connects.

#### 1.4 `Disconnect()` exact effects

Precondition: if `_live == null`, it does nothing at all (it returns before anything else). That includes **not** clearing agent arming (Q6).

In order:

1. (from fix-burn-after-reopen, not yet on main) Before stopping anything, if `_ecuConnection.UnburnedPages` is non-empty, build `unburned = "Disconnected with changes that were written but never burned (page " + join(", ", UnburnedPages) + "). They are in working memory only and will be lost when the controller restarts. "`. Otherwise `unburned = ""`. The word "page" stays singular even for several pages. `UnburnedPages` is the ascending union of pages with pending (written since the last burn) ranges and pages with lost ranges.
2. Stop the session (joins the poll thread) and dispose it. This closes the port.
3. Clear `_ecuConnection`, `_maxxUsbSource`, the MaxxECU tune definitions, `_obd2` and the OBD2 undecoded list.
4. **`AgentWritesArmed = false`.** Setting it raises the arming Hint (`Agent writes are off. The API can still read everything.`) if it was armed. Step 6 then overwrites that Hint when a document is open.
5. `LiveStatus = ""`. Port, signature, version, INI and recording path are cleared.
6. Hint: on main, if a document (log) is loaded, `Disconnected. The session is still here, and its recording is on disk.`; otherwise Hint is unchanged. On the fix branch: if a document is loaded, `unburned + "Disconnected. The session is still here, and its recording is on disk."`; otherwise, if `unburned` is non-empty, `unburned` with its trailing space trimmed.

`Disconnect()` **keeps** the tune (`_ecuTune`, `_tuneLayout`), the pending settings edit, the open table edit, `_settingsPagesWritten`, `Status` (the toolbar still says `Live — …`, see Q10) and `Title`. With `_ecuConnection` now null, every write and burn gate is false. Save and Compare stay available.

#### 1.5 TunerStudio connect sequence (`Connect(transport, port, settings)`)

1. `Disconnect()`.
2. Open the transport. Opening a Speeduino's serial port resets the board.
3. `ReadIdentity()`: ask every identity command once. If nothing answers, close and reopen the port, wait `BootWait` (2.5 s) and ask again, until `IdentifyWithin` (12 s). **Each of those reopens resets a Speeduino.**
4. Match the identity against every `.ini` on the definition search paths. **If none matches**, dispose the connection, create the definitions folder, record `DefinitionNeeded`, and fail with this text, shown in the window's `Could not connect on {port}.\n\n{text}` dialog:

   ```
   The ECU reports "{identity joined with "", ""}", and no definition file on this machine matches it.\n\n
   ```
   (or `The ECU did not say what it is.\n\n` when the identity list is empty), then always:
   ```
   A live ECU sends raw numbers with no names, units or scaling — all of that is in the .ini for that exact firmware. Without it the data cannot be decoded, and guessing would show readings that look right and are not.\n\n
   ```
   then, only if there are near misses: `The closest thing already here is:\n` (1 near miss) or `The closest things already here are:\n` (more), then one line per near miss, `    {name}  ("{signature}")\n`, then `\n`; then, if known, `The file you need is usually called {filename}.\n\n`; then, if known, `{where}\n\n`; then `Put it here and connect again:\n\n{folder}`.

   This refusal is the **signature guard**: no session ever decodes with an INI whose signature the ECU did not report.
5. If more than one definition declares the signature, the session proceeds but the connect Hint gets `AmbiguityNote()` appended: `  {n} definitions on this machine declare this signature ({names joined ", "}); this session is using {ini file name}.`
6. Remember the port with the version string (for the Reconnect label). Read the INI. Set the realtime request format, with `SingleRequestBlock` from settings.
7. `_ecuConnection = connection`. `_ecuSignature = signature`.
8. `ReadTuneFromEcu`. This clears every tune field, `OpenDialog`, open curves, the settings edit and `_settingsPagesWritten`. It sets `TuneIsPlaceholder = false` and `TuneIsFromFile = false`, and (from fix-burn-after-reopen, not yet on main) calls `connection.ForgetLost()`. It then reads every page. On success `EcuTuneSummary` is `{TotalSize:N0} bytes read from the ECU in {ms:N0} ms · {tables} tables · {settings:N0} settings[ · {dialogs:N0} pages]`. On failure the session continues **without a tune** and `EcuTuneSummary` is `The ECU's settings could not be read: {message}`.
9. Build the gauges. Start the live session, recording to a new workspace file only if `RecordOnConnect`.
10. Status `Live — {signature}   •   {channel count} channels   •   {ini file name}`. Title `Live: {signature} — OpenLogViewer`. Hint `{Opening(recording)} The plot follows the newest data until you zoom or pan.` + (if any channels: `  Untick "Hide unused" to see every channel — all of them are being recorded either way.`) + `AmbiguityNote()`.

`Opening(recording)` is `Recording to {path}.` or `Watching only — nothing is being written. Press Record when you want a log.`

Other connect kinds set these Status / Title / Hint texts:

| Kind | Status | Title | Hint |
|---|---|---|---|
| MaxxECU-BT | `Live — MaxxECU   •   {n} channels` | `Live: MaxxECU — OpenLogViewer` | `{Opening} A MaxxECU sends a fixed set of channels, and its tune cannot be read, so calibration is not available for it.` |
| MaxxECU USB | `Live — MaxxECU (USB)   •   {n} channels` | `Live: MaxxECU ({where}) — OpenLogViewer`, where `where` is `USB {serial}` or `USB` | `{Opening} Over USB a MaxxECU names every channel it sends, so this session found {count} of them by listening rather than by being told. {tuneNote}`. `tuneNote` is `Its tune came along too — {definitions:N0} settings from MTune's own definitions, ready to read or change.`, or `Its tune could not be read: MTune is not installed here, so there is nothing that names its settings or says where they live.`, or `Its tune could not be read: {message}` |
| SSM | `Live — SSM   •   {n} parameters   •   {adapter}` | `Live: SSM — OpenLogViewer` | `{Opening} Reading {n} parameter{s} over Subaru's own protocol — one address per request, so this updates about once a second. That suits what SSM is for: values the ECU has learnt, which move over minutes. Edit {SsmParameterFile.Name} in the definitions folder to change what is read.` |
| OBD2 | `Live — OBD2   •   {n} channels   •   {adapter or "OBD2"}` | `Live: OBD2 — OpenLogViewer` | `{Opening} ` + (batching ? `This car answers several parameters to one request, so a round of readings is two exchanges rather than six — still slower than a tuning cable, and much better than OBD2 usually manages. ` : `OBD2 asks for one parameter at a time, so this updates about twice a second rather than 25 times — the protocol's limit, not the link's. `) + `A standard vehicle has no tune to read, so calibration shows its fault codes instead.` + (gaps ? `  {Obd2Gaps}.` : "") |

The OBD2, SSM and MaxxECU-BT connects **do not clear** a tune that was already in hand (from an earlier ECU, a file, or a definition). It stays on screen, but with `_ecuConnection` null nothing can be written to it (Q3).

---

### 2. Every trigger that connects, disconnects, writes, burns, restores or clears

Every result string below goes to Hint via `Report` unless stated otherwise. "Checks, in order" are the method's own refusals, which return a message and send nothing. **Build these into the methods themselves and not only into button enable rules.** The agent API and scripted runs call the methods directly.

The shared refusal texts, referred to by letter:

- **P** = `This is a firmware definition rather than a tune — every value in it reads as zero, so nothing here may be sent to a controller.`
- **F** = `This tune was opened from a file rather than read off the controller, so it cannot be sent back. Read the ECU's own tune first.`
- **R** = `Read the ECU's own tune first. A restore is worked out against what the controller actually holds, and this is not that.`
- **NC** = `Not connected to an ECU.`
- **NT** = `No tune has been read.`
- **NCH** = `Nothing has been changed.` (from fix-burn-after-reopen, not yet on main: replaced everywhere by `NothingToSend()`, see 3.3)

Core messages that can appear inside `{e.Message}`:

- From `EcuConnection.WriteTunePage`: `That write ends at {offset+len} of a {page.Size} byte page.`, `Page {i} declares no write command, so this firmware cannot be written to.`, and the read-back mismatch `The ECU did not take that write: {len} bytes were sent to offset {offset} of page {i} and reading the same range back gave something else. Nothing has been burned, so a power cycle restores the ECU.`
- From the protocol: `The ECU refused the request (status 0x{XX}).` (a **refusal**), `The ECU did not reply.`, `The link to the ECU failed: {io message}`, `The reply stopped after {n} of {m} bytes.`, `The ECU declared a {n} byte reply, which is not usable.`, `The reply carried {n} bytes where {m} were asked for; it belongs to an earlier request.`
- From `BurnPage`: `Page {i} declares no burn command.`; an **unconfirmed** burn (no reply within `BurnTimeout` 8 s, one attempt only, never retried): `The ECU did not confirm the burn of page {i}: {inner message}\n\nIt may still have completed — a controller stops answering while it writes its flash, which looks the same from here as one that never got the command. Turn the ignition off and on and read the tune back: if the change is there, it was burned.`
- After a successful burn, `BurnPage` waits the firmware's `AfterBurnDelay` milliseconds before the next request.

#### 2.1 Table of triggers

| # | Action | UI trigger(s) | Enabled rule | Confirmation (title `OpenLogViewer`, OK/Cancel, default Cancel, OK proceeds) | Checks in order → success / failure messages | What is written |
|---|---|---|---|---|---|---|
| 1 | **Send table to ECU** | Calibration, tables half: button `Send to ECU` (tooltip `Writes the changed cells into the ECU's working memory. Takes effect immediately; lost at the next power cycle unless burned.`). Visible when `ShowEcuTables`. | `CanWriteTable` | The handler returns silently if no table edit is open. Body: `Send {cells} changed cell{s} of {edit.Name} to the ECU?\n\nThis takes effect immediately on a running engine.\n\nIt is not permanent: the ECU forgets it at the next power cycle unless you burn it.`, where `cells = TableEdit.ChangedCount`. | **P**; **F**; `No table is open.`; **NC** (only if both `_ecuConnection` and `_maxxUsbSource` are null); **NT**; **NCH**; `This table cannot be encoded for this firmware, so nothing was sent.`; → MaxxECU path (#2) if `_maxxUsbSource`; **NC**; `The firmware declares no page {p}.`; `This firmware declares no way to write page {p}.` Then one `WriteTunePage` (chunked at blocking factor − 8, `InterWriteDelay` between chunks, then a **read-back compare**). On success: the tune image and the settings edit both accept the write, the table view is re-read on the UI thread, and the message is `Sent {cells} changed cell{s} to the ECU. It is running this now, and will forget it at the next power cycle unless you burn it.` (fix: + `ResentNote`). On failure: `The write failed: {e.Message}` | The table's encoded cell bytes, as one contiguous run on its page. **Not added to `_settingsPagesWritten`.** |
| 2 | Send table, MaxxECU USB | Same button. **Unreachable on main**, because `CanWriteTable` needs `_ecuConnection` (Q1). Reached by the agent. | – | as #1 | `WriteTune(offset, data)`. If the status is not OK: `The MaxxECU answered "{status}" for the write at offset {offset}.` Then a read-back; on mismatch: `The MaxxECU took the write at offset {offset} but reads back something else now.` Success: `Sent {cells} changed cell{s} ({bytes:N0} bytes) to the MaxxECU, verified. There is no burn step on this controller — it is already running this, permanently, and a power cycle will not undo it.` + `OverlapWarning`. Failure: `The write failed: {e.Message}`. `OverlapWarning`, when the bytes overlap named settings: ` Warning: this table's cells share bytes with a named setting on this ECU ({first 5 names, each in "quotes", joined ", "}[, and {n−5} more]) — the table may never have been sized here, and this write may have changed that setting's value along with the cell you asked for.` | **Permanent at once.** No burn exists. A write larger than 256 bytes is refused, not split. |
| 3 | **Burn table** | Tables half: button `Burn` (tooltip `Commits the page to the ECU's flash. This survives a power cycle.`) | `CanBurn`. Lit whenever connected with a real tune, **even if nothing was sent** (Q4). | Returns silently if no table edit. Body: `Burn the page holding {edit.Name} to the ECU's flash?\n\nThis is permanent. A power cycle will not undo it.\n\nBurn with the engine stopped: the ECU pauses while it writes flash.` | `No table is open.`; **NC**; **NT**; `This table cannot be encoded for this firmware.`; `The firmware declares no page {p}.`; `This firmware declares no way to burn page {p}.` (**no P/F check in the method**: add one, Q4). `BurnPage`. On success: remove the page from `_settingsPagesWritten`, `KeepBurnedTune()`, and the message `Burned page {page.Index}. This survives a power cycle.` On failure: a not-refused (unconfirmed) burn gives `e.Message` alone; a refused burn gives `The burn failed: {e.Message}`; (fix) a page in `LostPages` gives `e.Message` alone, and all write gates are raised. | Burn command for the table's page. |
| 4 | **Send settings to ECU** | Settings half: button `Send to ECU` (tooltip `Writes the changed settings into the ECU's working memory. Takes effect immediately; lost at the next power cycle unless burned.`). Visible when `ShowSettingsPages`. | `CanWriteSettings` | The handler returns if `!CanWriteSettings`. Body: `Send {settings} changed setting{s} to the ECU?\n\n{bytes:N0} bytes across {pages} page{s}.\n\nThis takes effect immediately on a running engine.\n\nIt is not permanent: the ECU forgets it at the next power cycle unless you burn it.`, where settings, bytes and pages are `SettingsChangedCount`, `SettingsBytesToWrite` and `SettingsPagesToWrite`. | **NT** (no settings edit); **NC** (both null); **NT**; **NCH** (no changes); **NCH** (no writes); → MaxxECU path (#5); **NC**. (**No P/F check in the method**, Q4.) Each write in turn, each read back: a missing page gives `The firmware declares no page {p}.`; no write command gives `This firmware declares no way to write page {p}.`. Each landed write is accepted into the tune and its page added to `_settingsPagesWritten`. Success: `Sent {settings} setting{s} ({bytes:N0} bytes) to the ECU. It is running them now, and will forget them at the next power cycle unless you burn them.` (fix: + `ResentNote`). Failure with none landed: `Nothing was sent: {e.Message}`. Partial failure: `{done} of {total} writes reached the ECU ({bytes:N0} bytes) and then this failed: {e.Message} Some settings have changed and some have not. Nothing was burned, so turning the key off restores all of it.` In both cases the edit is then **reconciled**: whatever landed stops being pending. | Changed setting bytes, in runs, possibly across pages. |
| 5 | Send settings, MaxxECU USB | Same. **Unreachable on main** (Q1). Reached by the agent. | – | as #4 | As #2, per write. Success: `Sent {settings} setting{s} ({bytes:N0} bytes) to the MaxxECU, verified. There is no burn step on this controller — it is already running them, permanently, and a power cycle will not undo it.` None landed: `Nothing was sent: {e.Message}`. Partial: `{done} of {total} writes reached the ECU ({bytes:N0} bytes), verified, and then this failed: {e.Message} Those {done} are already applied and permanent — there is no burn to skip and no power cycle that undoes them.` | **Permanent.** Never added to `_settingsPagesWritten`. |
| 6 | **Burn settings** | Settings half: button `Burn` (tooltip `Commits the pages that were written to the ECU's flash. This survives a power cycle.`). **Also** the curve panel's `Burn` button (tooltip `Commits what has been sent to flash, so it survives a power cycle`). | `CanBurnSettings` | The handler returns if `!CanBurnSettings`. Body: `Burn {pages} page{s} of settings to the ECU's flash?\n\nThis is permanent. A power cycle will not undo it.\n\nBurn with the engine stopped: the ECU pauses while it writes flash.`, where `pages = SettingsPagesWritten` (the size of `_settingsPagesWritten`). | **NC**; **NT**; `Nothing has been sent, so there is nothing to burn.` Then for each page in ascending order: no page or no burn command → strike it off and continue; `BurnPage`; on an **unconfirmed** failure strike the page off **and** stop; on a **refused** failure keep the page and stop; on success strike it off and record it as burned. If none were burned and there was no error: `This firmware declares no way to burn the pages that were written.` Success: `KeepBurnedTune()` and `Burned {n} page{s} to flash. These settings now survive a power cycle.` Error with none burned: unconfirmed gives `e.Message`, refused gives `The burn failed: {e.Message}`. Error after some burned: `{n} page{s} were burned and then ` + (unconfirmed ? `this happened: ` : `this failed: `) + `{e.Message} The rest are in working memory and will be lost at the next power cycle.` Note `page{s} were`: the C# says "1 page were" (Q11). | Burn command per written page, and only those pages. |
| 7 | **Send curve** | Curve panel: button `Send curve to ECU` (tooltip `Writes the moved points to the controller's working memory`). Visible when `ShowCurves`. | `CanWriteCurve` | The handler returns if `!CanWriteCurve`. Body: `Send {moved} moved point{s}{what} to the ECU?\n\nIt takes effect at once. Nothing is burned, so a power cycle undoes it.`, where `moved` is the sum of `ChangedCount` over the open curves and `what` is ` of "{Title}"` when exactly one curve is open, otherwise empty. | **P**; **F**; `No curve is open.`; **NC** (needs `_ecuConnection`; there is no MaxxECU path); **NT**; **NCH**. Then every changed curve is encoded **before anything is sent**; one that cannot be encoded gives `"{Title}" cannot be encoded for this firmware, so nothing was sent.`; none planned gives **NCH**. Every planned page is checked first: `This firmware declares no way to write page {p}, so nothing was sent.` Then each write: accepted into the tune and the settings edit, page added to `_settingsPagesWritten`. Success: `Sent {moved} moved point{s} to the ECU. It is running this now, and will forget it at the next power cycle unless you burn it.` (fix: + `ResentNote`). Failure: `The write failed: {e.Message}`. The curves are rebuilt from the tune either way. | Both rows (values and breakpoints) of each changed curve. They are meant to land together, but a mid-sequence failure can leave them split. |
| 8 | **Restore a saved tune to the ECU** | Tools ▸ `_Restore a saved tune to the ECU…` (tooltip `Writes the settings that differ, leaving alone everything the file does not mention`) | `CanBurn` (Q4) | Step 1: file dialog `Restore a saved tune to the ECU`, filter `TunerStudio tune\|*.msq\|All files\|*.*`. Step 2: `PlanRestore(path)`; its result goes to Hint. Step 3: if there is no plan, stop; if the plan is empty, cancel it and stop (no dialog). Step 4 body: `Restore {file name} to the ECU?\n\n{differences:N0} settings would change:\n{listed}\n\n{bytes:N0} bytes across {pages} page(s).` + leftAlone + warning + `\n\nNothing is burned, so a power cycle undoes all of it.\nDo this with the engine stopped.` Here `listed` is the first 12 differences, each `    {difference.Summary}` joined by `\n`, plus `\n    …and {n−12:N0} more` when there are more than 12. `leftAlone` (if any missing) is `\n\n{missing:N0} settings this firmware has are not in the file. They are left exactly as the ECU holds them.` `warning` (if the signatures disagree) is `\n\nTHIS FILE IS FOR ANOTHER FIRMWARE.\nThe file says "{fileSig}" and the ECU says "{ecuSig}". Settings may not mean the same thing in both.` Cancel → `CancelRestore()`. | **Plan** checks: **R** (placeholder or from file); **NC**; **NT**; file read error `Could not read {file name}: {message}`. Plan result: signatures disagree → `{file} is a tune for "{fileSig}" and this ECU says it is "{ecuSig}". {plan.Summary}`, otherwise `{file}: {plan.Summary}`. `plan.Summary` is: empty and complete → `The ECU already holds this tune, setting for setting.`; empty and incomplete → `Nothing would change. {Shortfall}`; otherwise `{n:N0} setting{s} would change, {bytes:N0} bytes across {pages} page{s}.` + (incomplete ? ` {Shortfall}` : ""). `Shortfall` joins with a space: `1 setting the firmware declares is not in the file and would be left as the ECU has it.` / `{n:N0} settings the firmware declares are not in the file and would be left as the ECU has them.` and `{n:N0} in the file could not be stored and would be left alone too.` **Apply** checks: **R**; `Nothing has been planned.`; **NC**; **NT**; empty plan → `The ECU already holds this tune, so nothing was sent.` (fix: `NothingToSend()` if `HasLostWrites`); every page is checked **before the first byte**: `This firmware declares no way to write page {p}, so nothing was sent.` Then each write is accepted into the tune and the settings edit, and its page added to `_settingsPagesWritten`. Success: `Restored {settings:N0} setting{s} to the ECU — {bytes:N0} bytes across {pages} page{s}. It is running this now, and will forget it at the next power cycle unless you burn it.` (fix: + `ResentNote`). Failure: `The restore stopped after {done} of {total} writes: {e.Message} The ECU is now holding part of this tune and part of the old one. Nothing has been burned, so a power cycle puts back what was last burned.` Always afterwards: cancel the plan (it can never be pressed twice), reconcile the edit, refresh the open tune views. | Only the bytes that differ between the ECU image and the file laid **over** the ECU image. Settings the file omits are never written. **Nothing is burned**: the user must then use settings Burn (pages are now in `_settingsPagesWritten`). |
| 9 | **Erase fault codes** (OBD2) | Fault panel (Calibration tab while OBD2 is live; also Tools ▸ `_Fault codes…` window, enabled when `IsObd2Live`, tooltip `Read and clear the vehicle's trouble codes — OBD2 only`): button `Erase codes…` | Enabled only after a scan that found stored + pending > 0. Permanent codes alone do not enable it. It is disabled while a scan runs. | Body: `Erase {count} fault code{s} from the vehicle?\n\nThis clears more than the codes. The freeze frame — the record of what the engine was doing when the fault occurred — goes with them, and it cannot be recovered. So do the oxygen sensor test results and the readiness monitors, which the car has to re-earn over a full drive cycle before it can pass an emissions test.\n\nIt does not repair anything. A fault that is still present will set the code again.\n\nMost vehicles refuse this with the engine running.`, where count = stored + pending. | Panel status text `Erasing…`, button disabled. Sends OBD2 mode `04`, then reads mode `0A` (permanent). Exception → the panel status shows `e.Message` and the button is re-enabled. No OBD2 link → panel status `No OBD2 vehicle is connected.` Otherwise a **result dialog** (OK; information icon if erased, warning icon if not): not answered/refused → `The vehicle refused to erase its faults. Most will not do this with the engine running — try again with the ignition on and the engine off.` or `The vehicle did not answer the erase request, so nothing has been cleared.`; erased → `Erased. The stored and pending codes, the freeze frame and the readiness monitors are all gone — the car will need a full drive cycle before it can pass an emissions test.` or `Erased, but {n} permanent code{" is" or "s are"} still set. Only the controller can clear those, and it will not do so until it has watched the monitor pass.` Then it **re-scans automatically**. | OBD2 mode 04 (clears DTCs, freeze frame and monitors). |
| 10 | **Connect** (all kinds) | see 1.3: toolbar `Connect ▾` popup (tooltip `Read live data from an ECU over serial or Bluetooth`), Tools ▸ `_Connect` submenu, Reconnect button, Tools ▸ `{ReconnectLabel}`, **Ctrl+K**, command line (2.5) | Connect items are always enabled (except while the Connect button is disabled during a probe; the Tools submenu and Ctrl+K are **not** blocked then, Q7). The Reconnect menu item needs `CanReconnect`. | **None.** It disconnects any live session without asking. | see 1.3 / 1.5 | Opening the port. **On a Speeduino this resets the board**, so unburned RAM changes are lost. The menu path opens the port twice (probe, then connect). Connecting reads every tune page; it writes nothing. |
| 11 | **Disconnect** | Tools ▸ `_Disconnect` (enabled `IsLive`); toolbar button while it reads `Disconnect` (it toggles when `IsLive`); window close | `IsLive` (menu) | **None** (Q5) | see 1.4 | Closes the port. |
| 12 | **Check which of these are switched on** | Connect menu, under the `Paired — listed whether or not switched on` heading (tooltip `Listens for the LE adapters and tries the others. Takes a few seconds, because a paired port that answers nothing has to be waited out before it can be called dead.`) | always | none | Each paired serial port is labelled `answered at HH:mm` / `no answer at HH:mm`. Each BLE adapter is labelled `switched on at HH:mm` / `not heard at HH:mm`. Then the menu is reopened. A failure only goes to the run log: `Could not scan for devices: {exception}`. | **Opens every paired Bluetooth serial port** (2 attempts each). This can reset or disturb a device. |
| 13 | **Probe Holley Sniper** | command line `--probe-sniper` only | – | none | Wait cursor. On success the report has these lines: `Opened. ECU node discovered: {node}.`, `Device-info read: {n} bytes back.` or `Device-info read failed: {msg}`, `Telemetry poll: {n} raw bytes back (not decoded into channels — see SniperSession's own doc comment for why).` or `Telemetry poll failed: {msg}`, and the closing text `None of this has been confirmed against real hardware — see SNIPER_ECU_CLIENT_SPEC.md. A failure above may mean this implementation's protocol guesses are wrong, not that nothing is plugged in.` If it cannot open: `Could not open a Sniper dongle: {msg}`. The report goes to Hint. | Node discovery, a device-info read and a telemetry poll. No writes. |
| 14 | **Agent writes** (settings, a table cell, a batch apply) | over the agent API (another section) | `AgentWritesArmed` and the gates in 4.6 | **No dialog.** Arming is the consent. | see 4.6 | Through #4/#5 and #1/#2. **Never burns, never restores.** |
| 15 | Start / stop the agent API | toolbar `AI API: On` / `AI API: Off` button (the label shows the current state; tooltip `A local API an AI assistant or a script can watch this session through. Click to turn off; click again to turn back on`); Tools ▸ `_Agent API…` (tooltip `A local API an AI assistant or a script can watch this session through`) | always | none | Stop → `The agent API is off and the socket is closed.` (and arming cleared). Start → `The agent API is listening on {address}. Its token is in {path}.`, `Already listening on {address}.`, or `The agent API could not start: {message} Another program may already have that port.` | Nothing to the ECU. |
| 16 | Arm / disarm agent writes | toolbar toggle `Allow AI writes` and Tools ▸ `Allow AI _writes` (checkable). Both bound to `AgentWritesArmed`. Tooltip: `Lets an agent change settings in the ECU's working memory. It never burns, and this clears itself on disconnect` | `AgentIsRunning` | none | Arm → Hint `An agent may now change settings and table cells in the ECU's working memory. Nothing it does is burned, and this turns itself off when you disconnect.` Disarm → `Agent writes are off. The API can still read everything.` | Nothing directly. |

Revert controls (`Revert`, `Revert settings`, `Revert curve`, `Revert cells`, Esc in the table) only change the pending edit. They never touch the ECU.

#### 2.2 Not ECU writes, but easy to mistake for them

- **Open tune…** (toolbar, File menu, dropping an `.msq` file) loads tables **for log analysis only** (`LoadTune`). It never touches the ECU tune or the gates.
- **Save the tune to a file…** (Tools; enabled when `CanSaveTune`; dialog title `Save the tune`, filter `TunerStudio tune|*.msq`, default name `{yyyy-MM-dd_HH.mm.ss}.msq`) writes a file. Messages: `There is no tune to save.`; the placeholder refusal `This is a firmware definition rather than a tune — every value in it reads as zero, so there is nothing worth saving.`; `Saved {bytes:N0} bytes of settings to {file}.`; `Could not write {file}: {message}`.
- **Compare with a saved tune…** (enabled when `HasEcuTune`) reads only.
- **Open a saved tune…** loads a file tune (`TuneIsFromFile = true`). It **discards any pending settings edit, open table edit, open curves and `_settingsPagesWritten` without asking** (Q12).

#### 2.3 Keyboard

No key sends, burns, restores or erases. The only connection key is **Ctrl+K** (reconnect to the last ECU). Table keys (arrows, +/−, PgUp/PgDn, Esc) and curve keys only edit the pending change.

#### 2.4 Where the results appear

Every result string from #1–#8 goes to Hint (and the run log). Connect failures go to dialogs, except the quiet paths. Fault-erase results go to a dialog and the panel's own status line.

#### 2.5 Command-line connect triggers (all run after the window is shown)

`--connect PORT [--obd2]`, `--connect-ble NAME`, `--connect-wifi ADDRESS|auto`, `--connect-menu PORT` (goes through the menu path, including its dialogs), `--connect-ssm PORT` (failure shows a dialog), `--connect-maxx-usb [SERIAL]`, `--probe-sniper`, `--agent-api [PORT]`.

- `--connect`, `--connect-ble` and `--connect-wifi` are quiet: failures go to the run log only.
- `--connect-maxx-usb` shows a dialog when no serial is given and there is not exactly one device: `No MaxxECU was found over the D2XX driver. Check it is plugged in, powered, and that the FTDI driver is installed.` or `{n} MaxxECU devices were found over the D2XX driver. Pass a serial number to choose one: {serials joined ", "}`. A connect failure there calls `Disconnect()` and shows the exception message in a dialog.
- The agent API **always** starts at launch, on port 8765 unless `--agent-api N` gives another port. It is never armed at launch, and **no flag arms writes**.

---

### 3. Burn-after-reopen protection (from fix-burn-after-reopen, not yet on main)

**Why.** Opening a Speeduino's serial port resets the board, and anything written but not burned lives in RAM. A reopen between a Send and a Burn therefore loses the change. On main, the Burn then commits the *old* tune and reports success. The read-back that verified the write ran before the reset, so it cannot catch this.

Reopens happen in three places: `EcuConnection.Open` when not already open, `EcuConnection.Reopen` (identity retries), and the live session's `Recover` (a dropped link).

#### 3.1 Connection bookkeeping (Core, `EcuConnection`; all under the conversation lock)

- Every page has two byte-range sets: **pending** (written since the last burn) and **lost** (written, then taken by a reopen, and not written again). Ranges are half-open, sorted and merged.
- `Reopens` counts every open or reopen. It is incremented **before** the open is attempted, because an open that fails part way may already have reset the board.
- On every `Open` (when not already open) and every `Reopen`: all pending ranges move into lost, and pending is cleared.
- On a successful **and read-back-verified** `WriteTunePage(page, offset, data)`: subtract `[offset, offset+len)` from that page's lost set, then add it to the page's pending set. **Only the bytes written become current again.** Other lost bytes on the same page stay lost.
- `BurnPage(page)`: after the no-burn-command check and **before sending anything**, if the page has any lost range, throw a **refused** protocol error (`Refused = true`, failure kind Refused) with the message:

  `Nothing was burned: the changes written to page {page.Index} were lost when the connection was reopened. Send to ECU again, then burn.`

  After a successful burn, clear both the pending and the lost set for that page.
- `UnburnedPages` is the ascending union of pages with non-empty pending or lost sets. `LostPages` is the ascending list of pages with non-empty lost sets. `LostRanges(page)` returns a list of `(offset, length)`. `ForgetLost()` clears every lost set.
- `TunerStudioSource.Recover` waits `BootWait` (2,500 ms) after `Reopen()` and before its first read.

#### 3.2 View-model rules

- `LostWrites()` returns nothing if `TuneIsPlaceholder` or `TuneIsFromFile`, or if there is no `_ecuConnection` or no tune. Otherwise, for each lost page that exists in the tune, and each lost range that fits inside the page (`offset ≥ 0`, `length ≥ 1`, `offset+length ≤ page length`), it yields a write of **the tune image's bytes at that range**. Those are the values the user chose. Ranges that are out of bounds are skipped silently.
- `HasLostWrites = LostWrites().Count > 0`. Re-evaluate it every frame. **While it is true, all three Send buttons (table, settings, curve) are enabled**, even with no edits, subject to the rest of each gate and to the button being visible.
- `ResendLost(connection, layout)` is called **first inside every Send path**: table (TunerStudio path), settings (TunerStudio path), curve, and restore apply. It runs inside the same try block as the new writes. For each lost write: a missing page or no write command throws `This firmware declares no way to write page {p}.`. Otherwise it calls `WriteTunePage` and adds the page to `_settingsPagesWritten`, so the settings Burn lights up. It does **not** accept the bytes into the tune or the settings edit: the tune already holds them, and accepting into the edit would overwrite a pending change to the same setting. It raises every write gate afterwards, even on failure. It returns the number of bytes re-sent.
- `ResentNote(bytes)` is empty when `bytes == 0`, otherwise ` {bytes:N0} bytes sent earlier and lost when the connection was reopened were sent again too.` (leading space). It is appended to the success message of the table, settings, curve and restore paths.
- `NothingToSend()` replaces **NCH** in the table path (`!edit.HasChanges`), the settings path (`!edit.HasChanges` and also "no writes"), the curve path (`!HasCurveChanges`), and restore apply when the plan is empty and `HasLostWrites` is true. It returns `Nothing has been changed.` if there are no lost writes, no connection or no layout. Otherwise it calls `ResendLost` and returns `Nothing new was edited, but {bytes:N0} bytes sent earlier were lost when the connection was reopened. They have been sent again. Nothing is burned until you burn it.` If that throws (protocol, IO or invalid operation), it returns `Nothing was sent: {e.Message}`.
- **Nothing is ever re-sent automatically.** Bytes go out only when the user presses a Send button (or an agent write runs a Send path).
- When the tables-half **Burn** is refused because the page is lost, it raises all write gates and returns the bare message `Nothing was burned: the changes written to page {p} were lost when the connection was reopened. Send to ECU again, then burn.`
- When the **settings / curve Burn** hits a lost page, the burn is refused, so the page **stays** in `_settingsPagesWritten` and Burn stays lit. The message carries the refusal prefix: `The burn failed: Nothing was burned: the changes written to page {p} were lost when the connection was reopened. Send to ECU again, then burn.` If earlier pages in the same press had burned: `{n} page{s} were burned and then this failed: Nothing was burned: … then burn. The rest are in working memory and will be lost at the next power cycle.` (Q13)
- `ReadTuneFromEcu` (every TunerStudio connect) calls `connection.ForgetLost()`, because the tune about to be read is the controller's own.
- `Disconnect()` warns about unburned pages as given in 1.4, step 1/6. **That is the only warning the branch adds.** Other paths that reopen the port do not warn: switching ECU (where the warning *is* produced but overwritten at once), Ctrl+K while live, a live-session `Recover`, identity retries, and window close. In those cases the loss surfaces only as a Burn refusal and lit Send buttons (Q5).
- The confirmation dialogs were **not** changed on the branch. A Send pressed with only lost bytes therefore asks `Send 0 changed settings to the ECU?\n\n0 bytes across 0 pages.…`, `Send 0 changed cells of {name} to the ECU?…` or `Send 0 moved points of "{title}" to the ECU?…` (Q14).

---

### 4. Every other guard

#### 4.1 Tune provenance guards

- **Placeholder** (a definition opened without a tune): no Send, Burn, restore, save or agent write; agent tune reads return empty. Gates: every `CanWrite*`, `CanBurn`, `CanBurnSettings`, `CanApplyRestore`, `CanSaveTune`. Method refusals: **P** (table, curve), the save refusal, **R** (restore), and the agent refusal `the tune in hand is a placeholder` / `It came from a definition file rather than off the controller, and is all noughts.` `EcuTuneSummary` after opening a definition: `{ini file} — {tables} tables · {pages:N0} pages. ` + (not connected ? `No ECU is connected, so every value reads as zero.` : `Every value reads as zero — this is what the firmware offers, not what the attached ECU is set to, so nothing here can be sent to it.`). A definition with no pages: `{ini file} declares no pages of settings.` Read error: `Could not read {file}: {message}`.
- **From a file**: no Send, Burn, restore or agent write; save and compare are allowed. Refusals: **F**, **R**, and the agent refusal `the tune in hand came from a file` / `Read the ECU's own tune before writing to it.`
- **Opening a saved tune** picks the definition that fits best: an exact signature match always wins; otherwise the one the file fills most, and a definition the file leaves more than half empty is never used. Refusals: `{file} does not say which firmware it is for, so there is no way to know what its numbers mean.` and `{file} is a tune for "{signature}", and no definition here fits it. Put its .ini in {definitions folder} and try again.` A non-exact match adds to the summary: ` Read against {ini file} ("{ini signature}"), which is not the "{file signature}" this tune was saved from.`

#### 4.2 Firmware / signature guards

- **Connect**: refused unless an INI declares the reported signature (1.5 step 4). If several do, the ambiguity note is shown (1.5 step 5).
- **Restore**: a signature mismatch does **not** block, but it is stated in the plan Hint and in capitals in the confirmation dialog (#8).
- **Compare**: `{file} is a tune for "{fileSig}" and this is "{ecuSig}". They are different firmwares, so comparing them says little.`

#### 4.3 Page and size guards (before or while writing)

- No page with the write's index → refused (`The firmware declares no page {p}.`).
- A page with no chunk-write command → refused (several texts, see #1, #4, #7, #8).
- A page with no burn command → refused for a table burn, or skipped and struck off for a settings burn.
- A write that ends past the page → `That write ends at {end} of a {size} byte page.` (Core)
- The curve and restore paths check **every** page before the first byte. The settings path checks **each write as it goes**, so a later bad page leaves the earlier writes applied, which the partial-failure message reports.
- **Read-back verify** after every page-protocol write. On MaxxECU USB every write is read back and compared too.
- A MaxxECU write larger than 256 bytes is refused rather than split.
- A burn is sent exactly once, never retried, and waits up to 8 s.

#### 4.4 Value range guards (human edits)

- **Settings fields**: a value outside the firmware's declared range is **refused**, and the field shows the stored value again. The refusal reason goes to Hint, through the row's refused event. The reasons are:
  - `That cannot be set here.`
  - `This setting is not editable.`
  - `The firmware stores this as plain ASCII.`
  - `That is longer than the {columns} characters this field holds.`
  - `That is not one of the choices this firmware offers.`
  - `That is not a number.`
  - `{value} is outside {low} to {high} {units}`, with numbers in `F{digits}` (digits clamped to 0–6) in the current culture.
  - `The firmware will not hold that value.`

  **One exception:** a value outside the range is accepted when it equals, to within 1e−9, the value this session first read for that key. This keeps a firmware sentinel such as 255 against a declared 0–250 writable. NaN and infinite values are refused. A row is editable only for a number, choice or text constant that is on the controller.
- **Table cells**: every edit (add, scale, set, interpolate) is **clamped** into the declared range rather than refused. **A NaN or infinite result becomes 0, without the clamp** (Q15). The Set-value box refuses empty or unparsable text with Hint `Type a number to set the selected cells to.` A table that cannot be encoded is refused at Send.
- No RPM, dangerous-constant or magnitude guard applies to **human** writes or burns. The engine-stopped advice is only text in the burn and restore dialogs (Q16).

#### 4.5 Unsent / unburned changes on navigation

| Situation | What the C# does |
|---|---|
| Close the window with unsent edits or unburned pages | **Nothing warns.** There is no closing handler. On close, `Disconnect()` runs, and any Hint it sets is never seen (Q5). |
| Switch ECU / connect while live | Disconnects without asking. The pending edit survives until the new TunerStudio connect clears it (`ReadTuneFromEcu`); OBD2, SSM and MaxxECU connects leave it in place. |
| Disconnect with unburned pages | main: no warning. Fix branch: Hint warning (1.4). |
| Select another table with unsent cell edits | The edit is **discarded silently**: each table selection makes a fresh edit (Q12). |
| Open another settings page | Pending setting edits are **kept**: one edit spans every page of the tune. |
| Open a saved tune or a definition with pending edits | **Discarded silently**, along with `_settingsPagesWritten` (Q12). |
| Open a log while live | The document is replaced and the session keeps running. Gates are unaffected. |

#### 4.6 Agent API write gates, as the user meets them

The API starts **on** at every launch and is **read-only** until armed. The toolbar, right-hand side, shows:

- an 8 px dot: `Faint` when off, `Accent` when running, `Marker` while an agent write to the ECU is in flight or happened in the last 1.5 s;
- the `AI API: On/Off` button;
- the `Allow AI writes` toggle, enabled only while the API runs. Its tooltip is the `AgentSummary` text: `Off. No socket is open.` or `Listening on {address} — {n} watching, writes armed.` / `… read-only.`

The status bar's AI activity indicator (another section) shows only while the API runs.

Arming is never saved. It is false at start-up, and it is cleared by `Disconnect()` (when a session existed) and by stopping the API. It is **not** cleared by a live-session reconnect, and **not** by connecting from Disconnected (Q6).

Every agent write is refused, in this order, with these (reason, detail) pairs:

1. A blank rationale or note → `no rationale given` / `Say in one line why this change is being made. It is echoed back with the write.` (for a batch: `… It doubles as the version note.`)
2. Not armed → `writes are not armed` / `Tick "Allow agent writes" in the application. It clears itself on disconnect.` (Q17: the label is "Allow AI writes")
3. No TunerStudio or MaxxECU-USB link → `not connected to an ECU`
4. Placeholder or from-file refusals (4.1).
5. Live RPM above **1200** → `the engine is running above idle` / `RPM reads {rpm:0}, above the 1200 rpm guard. Writes are refused while the engine may be under load or being driven; try again at idle or with it off.` RPM is read from the live session's engine-speed role. If RPM is missing or not finite, the write is allowed.
6. Rate limit: 10 writes landed in the last 5 s → `too many writes in a short time` / `{n} agent writes landed in the last 5 seconds. Slow down, and check what each one did before sending the next.` A batch apply counts as **one** write, and the per-item check stands down while a batch runs.

After those come the per-item checks:

- No tune → `no tune has been read`.
- Unknown setting → `no such setting` / `This firmware declares no "{name}".`
- **Dangerous constant** without `confirmDangerous:true` → `this setting needs confirmDangerous:true` / `"{name}" matches the {role} guard — a setting whose job is to stop the engine hurting itself. Pass confirmDangerous:true if this change is intentional.` A constant counts as dangerous when it exists in the layout and its name, lower-cased with ` _ . - tab ° : /` removed, contains one of these fragments:
  - RevLimiter: `revlimiter revlimit revlim softlimit softlim hardlimit hardlim rpmlimit speedlimiter maxrpm limiterrpm rpmhardlimit rpmsoftlimit hardrevlimit softrevlimit kindoflimiting hardcut`
  - LaunchControlRpm: `launch lnch flatshiftrpm flatshift antilagrpm antilag clutchrpm twostep 2steprpm stagedlaunch`
  - BoostLimit: `boostlimit boostctrlmax overboost maxboost boostcut boostfailsafe wastegatemax boostlimitkpa boostcontrolmax boostbygear`
  - IgnitionCut: `ignitioncut igncut sparkcut coilcut shiftcuttime flatshiftign cuttimeign sparkcuttime`
  - FuelCut: `fuelcutrpm fuelcut overruncut decelfuelcutoff dfcorpm injectorcut fuelcutoff fuelcutoffrpm afrprotect`

  The roles are checked in that order and the first match wins. For a table cell, the check is against the constant behind the table.
- Value will not go in → `the value would not go in` / `It is out of the range the firmware declares, or the setting is not a number.`
- **Magnitude**: more than half the declared range in one call → `that change is too large for one write` / `Moving this by {delta:0.###} is more than half of its declared {low:0.###}–{high:0.###} range in a single call. Make the change in smaller steps, checking what happened after each one.` A refused setting is reverted from the pending edit. When there is no declared range, or the old value cannot be read, the change is allowed.
- Table cell out of bounds → `that cell is not in the table` / `"{table}" is {columns} by {rows}.`; unknown table → `no such table`; `that table cannot be edited`.
- The Send itself fails → `the write did not go through` / {the Send message}.

There is **no confirmation dialog** for agent writes. An agent setting write sends **every** pending setting change, including the human's unsent ones, and an agent cell write can send the human's unsent cells in the same table (Q18). Agents never burn (`Limits.Burns = false`).

#### 4.7 Session settings that affect the connection (app settings, not ECU)

- **Read the block in one request** (Tools, and the toolbar Connect popup; tooltips `Faster where the firmware allows it, fatal where it does not` and, in the popup, `Faster on a MegaSquirt, which serves its whole block despite declaring a smaller limit. A rusEFI asked for more than it declares stops responding until it is replugged.`). It applies from the **next** connection. Hint on: `The realtime block will be asked for in one request from the next connection. If the ECU stops responding, unplug it, switch this back off, and reconnect.` Hint off: `Back to reading the realtime block in blocking-factor pieces.`
- **Logging rate** (5, 10, 25 (default), 50, 100, 200 Hz). It applies to the next session; SSM is capped at 5 Hz. Hint: live → `Logging rate set to {rate:N0} Hz — it applies to the next connection.`; not live → `Logging rate set to {rate:N0} Hz.`
- **Record as soon as I connect** defaults to **off** (Q19).

#### 4.8 "Read-only" conventions

There is **no read-only mode** in the application. "Read-only" exists only as the unarmed agent API. Keeping a car-installed ECU read-only (for example the MicroSquirt on COM3) is an operator convention that nothing in the code enforces.

---

### 5. Status texts during these operations

| Moment | Where | Exact text |
|---|---|---|
| Serial probe | Hint | `Connecting to {port.Label}…` |
| Serial probe failed | Hint | `{port.PortName} did not answer.` |
| Wi-Fi open | Hint | `Connecting to the Wi-Fi adapter at {address or "the usual addresses"}…` |
| Wi-Fi failed | Hint | `The Wi-Fi adapter did not answer.` |
| Paired scan | Connect button | `Scanning…` (disabled), afterwards `Connect ▾` |
| Live | Connect button | `Disconnect` |
| Not live | Connect button | `Connect ▾` |
| Live, healthy | LiveStatus (`Accent`) | `● {port} · {signature} · {rate:F1} Hz` + (retries > 0 ? ` · {retries} retries` : "") + ` · ` + (recording ? `REC {rows:N0} rows` : `not recording`) |
| Reconnecting | LiveStatus (`Marker`) | `○ {port} · waiting for the ECU to come back…` |
| Faulted | LiveStatus then Hint | the session error text (1.3) |
| LiveStatus tooltip | `LiveDetail` | lines `Port      {port}`, `Firmware  {signature}`, `Build     {version}`, `INI       {ini path}`, `Channels  {n}` + (undecoded ? `  (+{n} not decoded)` : ""), `Recording {path or "not recording"}`; or `Not connected.` |
| Connected | Status, Title, Hint | 1.5 |
| Disconnected | Hint | 1.4 |
| After a write, burn or restore | Hint | the result strings in 2.1 and 3.2 |
| After a burn with a tuning project open | Hint | `KeepBurnedTune` sets `Kept …` or `Burned, but the project could not record it: {message}`. **The burn's own result string overwrites it straight away** (Q20). |
| Fault scan | fault panel status | `Asking the vehicle…`, then the scan summary, or the error. Nothing read: headline `Nothing was read.`, link `The adapter is connected but did not answer.` / `No OBD2 vehicle is connected.` |
| Fault erase | fault panel status | `Erasing…` |
| Record button / menu label | – | `● Record…` / `■ Stop recording`. While recording: foreground `Marker`, semibold. |
| Recording summary | – | `● Recording {file} · {rows:N0} rows · {seconds:F0} s`, `Saved {file}`, or `Not recording` |
| Recording results | Hint | `Recording to {path}.`, `Could not start recording: {message}`, `Saved {rows:N0} rows to {path}. The session is still live.`, `Nothing was being recorded.`, `Not connected.` The record dialog is titled `Record the live session to`, with filter `CSV log\|*.csv\|All files\|*.*` and the default name `{signature cleaned}-{yyyy-MM-dd_HH-mm-ss}.csv`. |
| Forget remembered ECUs | Hint | `No devices were remembered.` or `Forgot {n} remembered device{s}. Presets, filters and calculated channels are untouched.` (no confirmation) |
| Reconnect label | button and menu | `Connect: {first two words of the known ECU signature, or the port name}`; `Connect` when there is nothing remembered |

---

### Open questions

- **Q1 (user decision).** On main, a human cannot press Send for a MaxxECU over USB. `CanWriteTable` and `CanWriteSettings` require `_ecuConnection`, which a MaxxECU never has, yet the methods support MaxxECU and the agent can write. Should the Rust gates use "TunerStudio connection **or** MaxxECU USB source" (as the methods and the agent gate already do)? The MaxxECU USB connect is also command-line only; there is no menu entry.
- **Q2 (builder can settle; recommended fix).** `AdoptMaxxTune` does not reset `TuneIsPlaceholder`, `TuneIsFromFile`, `_ecuSignature`, `_tuneFile`, `_tuneSymbols` or `_settingsPagesWritten`. After opening a saved tune and then connecting a MaxxECU over USB, the MaxxECU's own tune reads as "from a file", so writes are refused, and a saved `.msq` carries the previous ECU's signature. Reset them as `ReadTuneFromEcu` does.
- **Q3 (user decision).** The OBD2, SSM and MaxxECU-BT connects leave a tune from a previous ECU on screen. Clear it?
- **Q4 (builder can settle).** `BurnTableToEcu` and `WriteSettingsToEcu` (and `BurnSettingsToEcu`) do not check placeholder or from-file themselves; only the button gates do. Put the checks in the methods. Separately (user decision): table Burn and Restore are enabled by `CanBurn`, which is true with nothing sent, so a table Burn can spend a flash erase on an unchanged page.
- **Q5 (user decision).** Nothing warns about unsent edits or unburned pages when the window closes, when switching ECU, or on Ctrl+K while live. The fix branch only puts a Hint on Disconnect, and that Hint is overwritten when the disconnect is part of a connect or a fault. Should closing or switching ask first (for example a Yes/No naming the unburned pages)?
- **Q6 (user decision).** Arming agent writes while disconnected carries into the next connection, because `Disconnect()` does nothing without a session. Arming also survives a live-session reconnect, which can be a key cycle on a different engine state. Clear arming on every connect?
- **Q7 (builder can settle).** While a menu probe or Wi-Fi open is in flight, only the toolbar Connect button is disabled. The Tools ▸ Connect submenu and Ctrl+K can start a second connect. Disable every connect trigger while connecting.
- **Q8 (builder can settle).** The Reconnect button, menu item and Ctrl+K use quiet errors. A failed reconnect shows nothing to the user (run log only). Show the same dialog as the menu path.
- **Q9 (builder can settle).** After a successful probe but a failed connect, Hint stays `Connecting to {port}…`.
- **Q10 (builder can settle).** `Disconnect()` leaves the toolbar Status saying `Live — …` and the title saying `Live: …`.
- **Q11 (builder can settle).** `{n} page{s} were burned…` reads "1 page were burned" for n = 1. Keep the exact text or fix the grammar?
- **Q12 (user decision).** These silently discard unsent edits: selecting another table, opening a saved tune, and opening a definition. Ask before discarding?
- **Q13 (from fix-burn-after-reopen, builder can settle).** The two burn paths word the lost-page refusal differently. The table Burn gives the bare message; the settings and curve Burn prefix it with `The burn failed: `. The commit message says both "show the refusal as a failure". Pick one.
- **Q14 (from fix-burn-after-reopen, user decision).** The Send confirmations were not updated, so a Send of only lost bytes asks to "Send 0 changed …". It needs text that names the re-sent bytes. The branch also gives the Send buttons no tooltip or label change to say why they are lit.
- **Q15 (user decision, safety).** Table edits (`TuneEdit.Hold`) turn a NaN or infinite result into **0, returned before the clamp**, so 0 is used even when it lies outside the declared range (and the encode may then refuse the whole table). `NaN` and `Infinity` typed in the Set-value box may parse as numbers in the current culture, which would set the selected cells to 0. Refuse non-finite input?
- **Q16 (user decision).** Human writes and burns have no RPM gate; only agent writes do. The burn and restore dialogs only advise "engine stopped". Add a gate?
- **Q17 (builder can settle).** The agent refusal says `Tick "Allow agent writes"`, but the control is labelled `Allow AI writes`.
- **Q18 (user decision, safety).** An agent write flushes the human's pending, unconfirmed settings (and, in the same table, unsent cells) to the ECU with no dialog. Should agent writes send only their own bytes, or refuse while the human has pending edits?
- **Q19 (builder can settle).** The "Record as soon as I connect" tooltip says `On by default. …` but the setting defaults to off.
- **Q20 (builder can settle).** The "Kept…" and "Burned, but the project could not record it…" Hints from `KeepBurnedTune` are overwritten at once by the burn result. Append them instead.
- **Q21 (builder can settle).** If the live session fails to start after a TunerStudio connect has assigned `_ecuConnection` (for example `No channels to record.`), the connection is disposed but `_ecuConnection` stays set. Write gates light up with no session and Disconnect is disabled. Clear the connection on any connect failure. A failure between opening the port and assigning `_ecuConnection` (for example reading the INI) also leaves the port open.
- **Q22 (user decision).** During Reconnecting, `IsLive` stays true and every write gate stays enabled. A Send during reconnect competes with the recovery for the port, and on a Speeduino it lands just before (or is lost to) a reset. Disable writes while reconnecting?
- **Q23 (builder can settle).** All writes, burns, restores and erases block the UI thread with no wait cursor and no progress text (a burn can take up to 8 s). Show an "in progress" state and block other triggers.

### Safety rules found

1. Send table (tables-half `Send to ECU`, gate `CanWriteTable`) needs the OK/Cancel dialog `Send {cells} changed cell{s} of {name} to the ECU?…`. Refuses placeholder or file tunes. Every write is read back and compared. Result `Sent {cells} changed cell{s} to the ECU. It is running this now, …` or `The write failed: {msg}`.
2. Send settings (settings `Send to ECU`, gate `CanWriteSettings`) needs the dialog `Send {n} changed setting{s} to the ECU?\n\n{bytes:N0} bytes across {pages} page{s}.…`. A partial failure is reported as partial (`{done} of {total} writes reached the ECU…`).
3. Send curve (`Send curve to ECU`, gate `CanWriteCurve`) needs the dialog `Send {moved} moved point{s}{ of "title"} to the ECU?…`. Every curve is encoded and every page checked before the first byte.
4. Burn table (`Burn`, gate `CanBurn`) needs the dialog `Burn the page holding {name} to the ECU's flash?…`. Sent once, never retried. Unconfirmed gives the "may still have completed" text; refused gives `The burn failed: …`.
5. Burn settings and curve (`Burn`, gate `CanBurnSettings`) need the dialog `Burn {pages} page{s} of settings to the ECU's flash?…`. Only written pages are burned, each struck off as it lands or goes unconfirmed; a refused page is kept.
6. Restore (Tools menu, gate `CanBurn`) plans first and lists up to 12 differences. Signature mismatch: `THIS FILE IS FOR ANOTHER FIRMWARE.` Only differing bytes are written, nothing is burned, the plan is single-use, and every page is checked first.
7. Erase fault codes (`Erase codes…`, enabled after a scan with stored + pending > 0) needs a dialog naming the freeze frame and readiness loss, then rescans.
8. Connect refuses any ECU whose signature matches no INI (missing-definition text). Several matches give an ambiguity note.
9. Every connect disconnects the current session without asking. Opening a Speeduino port resets it and loses unburned RAM changes.
10. Disconnect clears agent arming (only when a session existed) and closes the port. (from fix-burn-after-reopen, not yet on main) It warns `Disconnected with changes that were written but never burned (page …)…`.
11. (from fix-burn-after-reopen, not yet on main) A burn of a page whose written bytes were lost to a reopen is refused before sending: `Nothing was burned: the changes written to page {p} were lost when the connection was reopened. Send to ECU again, then burn.`
12. (from fix-burn-after-reopen, not yet on main) Lost bytes are tracked by byte range. Every Send path re-sends them first from the tune image, with `ResentNote` in the result. The Send buttons stay enabled while `HasLostWrites`. Nothing is re-sent without a Send press. A tune read forgets them.
13. MaxxECU USB writes are permanent at once (no burn), read back and verified, refused above 256 bytes, and warn when table bytes overlap named settings.
14. Agent writes need: the API running, `Allow AI writes` armed (never saved; off at start-up, cleared on disconnect or API stop), a rationale, a real tune, RPM ≤ 1200, at most 10 writes per 5 s, `confirmDangerous` for limiter, launch, boost and cut constants, and a change of at most half the declared range. They never burn.
15. Settings fields refuse out-of-range values (except the exact value first read). Table edits clamp into range, and a non-finite result becomes 0 (Q15).
16. Placeholder and from-file tunes can never be sent, burned or restored, or written by an agent.
17. Window close and ECU switch give no unsent or unburned warning (Q5). Selecting another table or opening a tune silently discards unsent edits (Q12).
18. An agent write flushes the human's pending, unconfirmed edits to the ECU (Q18).


## §9 `w9-shell`: main window, menus, toolbar, status bar, Guide, Insights, command line

### Scope

The main window frame and everything in it that no mode screen owns: the window itself, the menu bar
(with the AI cluster docked at its right), the toolbar, the status bar, the mode switch, the interface
zoom, theme picking, drag-and-drop, global keys, the Guide mode screen, the Insights window, closing,
first-run state, and the command line. The content of the Log / Histogram / Scatter / Gauges /
Calibration screens belongs to other sections; this section only says when each is on screen.

Source files: `MainWindow.xaml` (frame + Guide), `MainWindow.xaml.cs`, `App.xaml.cs`, `App.xaml`
(menu templates only), `InsightsWindow.xaml(.cs)`, `ToolbarFlowPanel.cs`, `InterfaceScale.cs`,
`TitleBar.cs`, `WorkspaceMode.cs`. View-model names are given in backticks where the w8 builder needs them.

Throughout, "Report(x)" means: set the status-bar hint to `x` (`MainViewModel.SetHint`) AND append
`x` to the run log (see "Run log" below). "Warning box" means a modal message box with title
`OpenLogViewer`, a warning icon and a single **OK** button, unless stated otherwise.

---

### The main window

| Property | Value |
|---|---|
| Title | `MainViewModel.Title` (rule below) |
| Icon | the application icon (`AppIcon.ico`) |
| Design size | 1280 wide x 760 tall (logical px, before interface zoom) |
| Minimum size | 760 wide x 420 tall |
| Start position | centred on the screen |
| Background | token `Bg` |
| Accepts file drops | yes (see Drag-and-drop) |

**Fit to the screen at start-up.** Before the window is first placed:
`width = max(760, min(1280, workArea.width - 40))`, `height = max(420, min(760, workArea.height - 40))`,
where `workArea` is the primary screen's usable area (screen minus task bar) in logical px. Then it is
centred. This stops a window taller than a laptop screen opening with its title bar above the top edge.

**Size and position are NOT persisted.** Every launch opens at the fitted design size, centred, not
maximised. Nothing about window placement is saved for the main window or the Insights window.

**Title bar colouring** is owned by the theming section; it is applied as soon as the native window
exists so the frame never flashes white.

#### Window title rule

`Title` is a plain string that is only ever *set* at these moments; nothing else changes it:

| Event | Title becomes |
|---|---|
| Start-up | `OpenLogViewer` |
| A log file is loaded (menu, Ctrl+O, drop, command line) | `{fileName} — OpenLogViewer` (`{fileName}` = file name with extension, no folder) |
| Live session started on a TunerStudio-protocol ECU (MegaSquirt, rusEFI, Speeduino…) | `Live: {signature} — OpenLogViewer` (`{signature}` = the firmware signature string the ECU reported, in full) |
| Live MaxxECU over its serial/Bluetooth COM port | `Live: MaxxECU — OpenLogViewer` |
| Live MaxxECU over USB (D2XX) | `Live: MaxxECU ({where}) — OpenLogViewer` (`{where}` = the device's USB serial/description used as the port name) |
| Live OBD2 (serial, BLE or Wi-Fi) | `Live: OBD2 — OpenLogViewer` |
| Live Subaru SSM | `Live: SSM — OpenLogViewer` |

The separator is an em dash with a space either side (`" — "`).
Loading or clearing a **tune** (.msq) does not change the title. Disconnecting does not change the title.

**Open question:** after Disconnect the title keeps saying `Live: … — OpenLogViewer` although nothing is
live any more (the session's data stays on screen). Spec ambiguity a builder can settle only with the
user's word; the C# behaviour is "unchanged". Implement as C# unless told otherwise.

#### Overall layout (top to bottom)

1. **Menu row** — full width, background `Chrome`. Menu bar on the left; the AI cluster docked right.
2. **Toolbar** — full width, background `Panel`, 1 px bottom border `Line`, padding 10 left/right, 6 top/bottom.
3. **Mode area** — fills everything between toolbar and status bar. Exactly one of the four mode
   screens is visible (Log, Gauges, Calibration, Guide). Within Log mode, one of three log views
   (Plot, Histogram, Scatter).
4. **Status bar** — full width at the bottom, background `Chrome`, 1 px top border `Line`, padding 12 left/right, 5 top/bottom.

The interface zoom (below) scales all of this uniformly.

---

### Modes and log views

There are **four modes** (`MainViewModel.Mode`, enum `WorkspaceMode`): `Log`, `Gauges`, `Calibration`,
`Guide`. Default `Log`. And **three log views** (`LogView`): `Plot`, `Histogram`, `Scatter`, default
`Plot`. The log view is remembered independently of the mode: switching to Gauges and back to Log
returns to the same log view. (The brief's "six modes" are these 4 + 3, with Log/Plot being one.)

Boolean facades used by menus and chips (each is a radio: setting it true selects it; setting it
false does nothing, so clicking an already-selected one leaves it selected):

| Facade | True when | Setting true does |
|---|---|---|
| `InLogMode` | Mode = Log | Mode = Log |
| `InGaugeMode` | Mode = Gauges | Mode = Gauges |
| `InCalibrationMode` | Mode = Calibration | Mode = Calibration |
| `InGuideMode` | Mode = Guide | Mode = Guide |
| `ShowLog` | LogView = Plot | LogView = Plot |
| `ShowHistogram` | LogView = Histogram | LogView = Histogram |
| `ShowScatter` | LogView = Scatter | LogView = Scatter |
| `OverlaidLanes` | not stacked | stacked = false |
| `StackedLanes` | stacked | stacked = true / false |

**Mode shortcuts:** there are **no** keyboard shortcuts for modes or log views other than the menu
access keys (Alt, V, then L / G / C / U for modes, P / H / A for views). Mode is also changed by:
Help ▸ How to use this app (→ Guide); Tools ▸ Open a saved tune… (→ Calibration, even if the open
then fails); clicking a histogram cell or scatter mark (→ log view Plot, mode unchanged).
Mode and log view are **not persisted**; every launch starts in Log / Plot / overlaid.

Note: `ShowLog` etc. are true regardless of mode (they describe the log view, not the mode). This
matters for several enable rules below.

---

### The menu row

#### AI cluster (docked at the right end of the menu row)

Right margin 10 px, vertically centred. Hovering anywhere on the cluster that is not one of its two
buttons shows tooltip `MainViewModel.AgentSummary`:
- API off: `Off. No socket is open.`
- API on: `Listening on {address} — {n} watching, writes armed.` or `… — {n} watching, read-only.`
  (`{address}` = `http://127.0.0.1:{port}`, `{n}` = number of connected agent subscribers, integer).

Left to right:

| Element | Look | Behaviour |
|---|---|---|
| Light | circle 8x8, 6 px gap to its right | Fill `Faint` when API off; `Accent` when `AgentIsRunning`; `Marker` while `AgentIsWritingToEcu` (wins). |
| API toggle button | text `AI API: On` when running, `AI API: Off` when not; padding 8,2 | Tooltip: `A local API an AI assistant or a script can watch this session through. Click to turn off; click again to turn back on`. Click: if running → stop the API and Report(`The agent API is off and the socket is closed.`); else start it on port 8765 and Report(result of start, see below). No confirmation either way. |
| `Allow AI writes` toggle | toggle-button style `ToolbarToggle`, 6 px left margin, padding 8,2; shows pressed when armed | Two-way on `AgentWritesArmed`. Enabled only when `AgentIsRunning`. Tooltip: `Lets an agent change settings in the ECU's working memory. It never burns, and this clears itself on disconnect`. |

**Open question:** the view-model comment says the toggle's label is "the action a click takes, not the
current state", but the text shown is the state (`AI API: On` while on). Builder: show the C# text.

Starting the API (`StartAgentApi(port)`) returns, for Report:
- already running: `Already listening on {address}.`
- started: `The agent API is listening on {address}. Its token is in {path}.` (`{path}` =
  `{dataFolder}\agent-api.json`, which is (re)written with address, websocket URL and a fresh 64-hex token)
- failed: `The agent API could not start: {exception message} Another program may already have that port.`

Stopping deletes `agent-api.json`, disarms writes, clears the activity indicator.

Arming/disarming writes sets the hint:
- armed: `An agent may now change settings and table cells in the ECU's working memory. Nothing it does is burned, and this turns itself off when you disconnect.`
- disarmed: `Agent writes are off. The API can still read everything.`

Writes are disarmed automatically on every Disconnect and when the API stops; the armed state is never saved.

#### Menu bar visuals (applies to the menu bar, every drop-down and every submenu)

- Menu bar padding 6 left/right, 1 top/bottom; transparent over `Chrome`; items laid out horizontally.
- Font: the theme's `UiFont` (menus must not fall back to the system font).
- Each menu row: padding 10 horizontal, 6 vertical; rounded with radius token `Corner`; background
  transparent, `Hover` while highlighted (mouse over or keyboard focus).
- Each row has three columns: a **tick column 20 px wide** (shows `✓` in `Accent`, centred, only when the
  item is checked), the header text, and an **arrow column** showing `›` in `Muted` (10 px margin both
  sides) only when the item has a submenu.
- Top-level names on the bar (File, View, Tools, Help) have no tick column and no arrow; their list
  opens **below** the name, left-aligned, 2 px down. Submenus open to the **right** of their row,
  overlapping it by 3 px horizontally and shifted 8 px up.
- Drop-down panel: background `PanelAlt`, 1 px border `Line`, corner radius token `CornerLarge`,
  padding 4. Keyboard Up/Down cycles (wraps around) within a list.
- Separators: 1 px line in `Line`, margin 8 horizontal, 4 vertical.
- **Disabled items are drawn in `Muted`** (not greyed-out-looking) because many disabled items are used
  as captions/notes inside menus.
- Text after `_` in a header is the access key (underlined when Alt is held); a literal underscore in
  generated headers (device names) is doubled so it shows as `_`.
- **Shortcut text is not drawn.** The C# gives three items a gesture text (Ctrl+O, Ctrl+F, Ctrl+K) but
  the menu template has no column for it, so it never appears. **Open question:** builder may add a
  right-aligned gesture column, or reproduce the C# (no gesture shown). Only the Zoom submenu shows its
  shortcuts, because they are written into the header text itself.

#### Menus that scroll (exact rule)

Every drop-down and submenu of the **menu bar** is wrapped in a vertical scroller whose maximum height
is the height of the screen work area (logical px, not reduced by the zoom). If the list (after zoom
scaling) is taller than that, a vertical scroll bar appears and the list scrolls; otherwise no bar is
shown. This exists because at 150% zoom the View menu ran off the bottom of the screen and took the
Zoom submenu (the way back) with it.

The toolbar's **Connect ▾ pop-up menu** and all right-click context menus do **not** scroll (no such
wrapper). Context menus use the same panel look (`PanelAlt`, `Line` 1 px, `CornerLarge`, padding 4) and
are scaled by the zoom.

#### File menu (`_File`)

| # | Header (access key underlined by `_`) | Enabled when | Checked | Action / tooltip |
|---|---|---|---|---|
| 1 | `_Open log…` | always | – | Opens the log file dialog (see "Opening a log"). |
| 2 | `Open _tune…` | always | – | Tooltip `Use tables from a .msq file instead of the one stored in the log`. Opens tune dialog (see "Opening a tune"). |
| 3 | `Compare against another log…` | `HasDocument` (a log/live document exists) | – | Tooltip `Open a second log and show what changed between them`. Open dialog title `Compare against`, filter `Datalogs` = `*.mlg;*.msl;*.csv;*.tsv;*.txt`, then `All files`. On OK: Report(`MainViewModel.LoadComparison(path)`). |
| 4 | `Stop comparing` | `HasComparison` | – | Clears the comparison; Report(`Stopped comparing.`). |
| 5 | `Show the difference` | `HasComparison` | checkable, two-way `ShowDifference` | Tooltip `Subtract the second log's table from this one, cell by cell`. |
| – | separator | | | |
| 6 | `Use the tune stored in the log` | `UsingLoadedTune` (tables currently come from an opened .msq) | – | `ClearTune()`, then rebuild the histogram/scatter. |
| – | separator | | | |
| 7 | `_Export` ▸ (submenu, below) | `CanExport` (= a document exists) | – | |
| 8 | `_Data folder` ▸ (submenu, below) | always | – | |
| – | separator | | | |
| 9 | `E_xit` | always | – | Closes the main window (see Closing). |

**Export submenu** — items are shown or hidden by the current *log view* (not by mode):

| Header | Shown when | Enabled when | Action |
|---|---|---|---|
| `Plotted channels as CSV…` | ShowLog | `CanExportPlotted` (at least one channel ticked for plotting) | CSV of plotted channels; suggested name `{stem}-plotted.csv` |
| `All channels as CSV…` | ShowLog | always | CSV of all channels; `{stem}-channels.csv` |
| `Plot as PNG…` | ShowLog | always | Image of the plot view; `{stem}-plot.png` |
| `Table as CSV…` | ShowHistogram | always | `{stem}-table.csv` |
| `Sample counts as CSV…` | ShowHistogram | always | `{stem}-counts.csv` |
| `Table as PNG…` | ShowHistogram | always | image of the heat table; `{stem}-table.png` |
| `Plotted points as CSV…` | ShowScatter | always | `{stem}-points.csv` |
| `Scatter as PNG…` | ShowScatter | always | image of the scatter; `{stem}-scatter.png` |
| separator | ShowLog | | |
| `CSV covers the {ExportScope}` | ShowLog | **never** (caption) | `{ExportScope}` = `whole log`, or `marked span, {n:N0} samples` when a span is marked on the plot (n = last − first + 1). |

`{stem}` = log file name without extension, or `log` when the document has no file (live).
Every export first asks with a Save dialog: no custom title (system default), filter
`CSV file|*.csv|All files|*.*` or `PNG image|*.png|All files|*.*`, adds the extension, prompts before
overwriting, starts in the workspace's Exports folder (created if missing). Cancel = nothing happens.
The view model reports success in the hint. PNG exports paint the theme background (`Bg`) behind the view.
Failure (I/O, permission, unsupported path, invalid operation) → warning box:
```
Could not save the file.

{exception message}
```

**Data folder submenu**

| Header | Enabled | Action |
|---|---|---|
| `Open the folder` | always | Opens the workspace root folder in the OS file manager (creating it if needed). |
| `Change the folder…` | always | Tooltip `Where recordings and exports are kept`. Folder picker titled `Where should recordings and exports go?`, starting in the current root. On OK: set the data folder; on an unwritable folder, warning box with the message `{folder} cannot be written to.`; on success hint `Recordings and exports now go to {root}.` Existing files are not moved. |
| separator | | |
| `{DataFolder}` (the full current path) | never (caption) | – |

Opening any folder that fails → warning box `Could not open the folder.\n\n{message}`.

#### View menu (`_View`)

| # | Header | Enabled when | Checked when | Action / tooltip |
|---|---|---|---|---|
| 1 | `_Log` | always | InLogMode | Mode = Log. Tooltip `Recordings: the plot and the heat table` |
| 2 | `_Gauges` | always | InGaugeMode | Mode = Gauges. Tooltip `Live dials, as the firmware defines them` |
| 3 | `_Calibration` | always | InCalibrationMode | Mode = Calibration. Tooltip `Work on the tune itself` |
| 4 | `G_uide` | always | InGuideMode | Mode = Guide. Tooltip `How to use this application` |
| – | separator | | | |
| 5 | `_Plot` | InLogMode | ShowLog | log view Plot. Tooltip `Time-series view` |
| 6 | `_Histogram` | InLogMode | ShowHistogram | Tooltip `Bin the log into a table, like a tuning table` |
| 7 | `Sc_atter` | InLogMode | ShowScatter | Tooltip `Every sample at its own X and Y, coloured by a third channel` |
| – | separator | | | |
| 8 | `_Overlaid traces` | ShowLog | OverlaidLanes | Tooltip `All traces share the plot — best for comparing timing between channels` |
| 9 | `_Stacked traces` | ShowLog | StackedLanes | Tooltip `Each channel gets its own strip — best when several are plotted` |
| – | separator | | | |
| 10 | `_Find in the log…` | ShowLog | – | Tooltip `Jump to where a condition held — RPM > 4000 && TPS > 80`. If the find bar is closed, open it and focus its box; if open, just focus its box. (Find bar belongs to the Log section.) |
| 11 | `_Reset zoom` | ShowLog | – | Live session: resume following the newest data (no zoom change otherwise). Not live: reset the plot to the full log. |
| 12 | `Draw steady channels as steady` | always | checkable, **starts checked**, not persisted | Tooltip `A sensor that barely moves is drawn flat rather than having its last decimal stretched to fill the lane. Turn off to see the true range`. Toggles the plot's global hold-steady flag and redraws the plot. |
| – | separator | | | |
| 13 | `_Units` ▸ | always | – | built on open (below) |
| 14 | `_Theme` ▸ | always | – | built on open (below) |
| 15 | `_Zoom` ▸ | always | – | built on open (below) |

(Items 1-4 behave as radio items: clicking the checked one keeps it checked.)

**Open question:** items 8-11 are enabled by `ShowLog` alone, so they are enabled in Gauges/Calibration/
Guide mode whenever the remembered log view is Plot (where the plot is not visible). Likely an oversight;
builder can settle (suggest `InLogMode && ShowLog`) only with the user's agreement — the C# rule is `ShowLog`.

**Units submenu** (rebuilt every time it opens; same list as in the Connect pop-up):

| Header | Checked when | Action |
|---|---|---|
| `As reported  (default)` (two spaces) | Units = AsReported | set Units |
| `Metric  (°C, km/h, kPa)` | Units = Metric | set Units |
| `Imperial  (°F, mph, psi)` | Units = Imperial | set Units |
| separator | | |
| `Display only — recordings keep the units the ECU reported` | disabled caption | – |

Units are persisted (settings file) by the view model.

**Theme submenu** (rebuilt every time it opens): one checkable item per theme, in catalogue order,
with a separator inserted wherever the theme group changes. Groups in order: Dark, Light, Editor,
High contrast, Retro. Current catalogue:

`Midnight`, `Graphite` | `Daylight`, `Paper` | `Dracula`, `Nord`, `Solarized Dark`, `Solarized Light`,
`Monokai`, `One Dark`, `Gruvbox Dark`, `Tokyo Night` | `High Contrast Dark`, `High Contrast Light` | `Retro DOS`

(`|` = separator.) The current theme (`SelectedTheme`) shows the tick. Clicking an item applies that
theme immediately to every window and recolours plot traces (palette belongs to the theme), closes
the menu, and **persists** the theme id. There is **no hover preview**; the only "preview" is the
`--theme` command-line switch, which applies a theme for that run without saving it. Unknown id →
default theme `midnight`. Token values belong to the theming section.

Note: selecting the theme that is already active is a no-op (also does not persist; so after
`--theme X`, choosing X from the menu does not save X).

**Zoom submenu** (rebuilt every time it opens):

| Header (exact, spaces matter) | Enabled when | Checked | Action |
|---|---|---|---|
| `Zoom _in    Ctrl +` (4 spaces before Ctrl) | scale < 2.0 | – | next larger step |
| `Zoom _out   Ctrl −` (3 spaces; `−` is U+2212 minus sign) | scale > 1.0 | – | next smaller step |
| `_Actual size   Ctrl 0` (3 spaces) | scale ≠ 1.0 | – | scale = 1.0 |
| separator | | | |
| `100%` | always | when scale = 1.0 | scale = 1.0 |
| `115%` | always | when scale = 1.15 | scale = 1.15 |
| `125%` | always | 1.25 | |
| `150%` | always | 1.5 | |
| `175%` | always | 1.75 | |
| `200%` | always | 2.0 | |

Step labels are `{scale*100:0}%`.

#### Tools menu (`_Tools`)

| # | Header | Enabled when | Checked | Action / tooltip |
|---|---|---|---|---|
| 1 | `_Connect` ▸ | always | – | Built on open from the same code as the toolbar pop-up, **without** the session-settings tail (see "The connect list"). |
| 2 | `{ReconnectLabel}` | `CanReconnect` = not live AND a remembered ECU exists | – | Tooltip `Reconnect to the ECU you used last`. Same as Ctrl+K. |
| 3 | `_Disconnect` | `IsLive` | – | Stops the session (see Disconnect). |
| 4 | `_Forget remembered ECUs` | always | – | Tooltip `Clears the list of devices this has connected to. Presets, filters and calculated channels are untouched`. Report: `No devices were remembered.` or `Forgot {n} remembered device{s}. Presets, filters and calculated channels are untouched.` (`{s}` = `s` unless n = 1). No confirmation. |
| 5 | `_Fault codes…` | `IsObd2Live` (live over OBD2) | – | Tooltip `Read and clear the vehicle's trouble codes — OBD2 only`. Opens the Fault codes window (owned by another section); if already open, restore it from minimised, bring to front, and re-scan. The window is not kept after closing. |
| – | separator | | | |
| 6 | `{RecordLabel}` = `● Record…` / `■ Stop recording` | `CanRecord` (= IsLive) | – | Tooltip `Choose when a log starts, what it is called and where it goes`. See Recording. |
| 7 | `Record as soon as I _connect` | always | checkable, two-way `RecordOnConnect` (persisted, default on) | Tooltip `On by default. Turn it off to pick the moment a log starts yourself — the session is still watched and plotted either way` |
| 8 | `Open the _recordings folder` | always | – | Opens the workspace Logs folder (created if missing). |
| – | separator | | | |
| 9 | `Logging _rate` ▸ | always | – | Rate list, built on open (below). |
| 10 | `Read the block in one request` | always | checkable, two-way `SingleRequestBlock` (persisted) | Tooltip `Faster where the firmware allows it, fatal where it does not` |
| – | separator | | | |
| 11 | `_Save the tune to a file…` | `CanSaveTune` (an ECU tune with a known layout is in hand and is not a placeholder) | – | Tooltip `Write every setting to a .msq — a backup, and the format TunerStudio reads`. Save dialog title `Save the tune`, filter `TunerStudio tune|*.msq`, default ext `.msq`, suggested name `{now:yyyy-MM-dd_HH.mm.ss}.msq`. On OK: Report(`SaveTuneToFile(path)`). |
| 12 | `_Open a saved tune…` | always | – | Tooltip `Open a .msq and its firmware's settings pages, with no ECU attached`. Open dialog title `Open a saved tune`, filter `TunerStudio tune|*.msq|All files|*.*`, must exist. On OK: Mode = Calibration; if `OpenSavedTune(path)` succeeds, show settings pages; Report(`EcuTuneSummary`). |
| 13 | `Co_mpare with a saved tune…` | `HasEcuTune` | – | Tooltip `Says which settings the file and the tune in hand disagree about`. Dialog title `Compare with a saved tune`, same filter. Report(`CompareWithSavedTune(path)`). |
| 14 | `_Restore a saved tune to the ECU…` | `CanBurn` (connected TunerStudio-protocol ECU, layout known, tune not a placeholder and not opened from file) | – | Tooltip `Writes the settings that differ, leaving alone everything the file does not mention`. See "Restoring a tune" — **safety**. |
| – | separator | | | |
| 15 | `_ECU definition files…` | always | – | Tooltip `Where to put the .ini for an ECU this machine does not already know`. Opens the definitions folder (created if missing). |
| – | separator | | | |
| 16 | `Ca_lculators…` | always | – | Tooltip `Boost, pressure ratio, injectors, fuel pump, lambda, octane, engine, gearing and airflow`. Opens/raises the Calculators window (same as toolbar button). |
| 17 | `Estimate _power…` | `HasDocument` | – | Tooltip `Calculated channels that estimate horsepower from the log`. Opens the Power window, kept alive; if open: restore from minimised and bring to front. |
| – | separator | | | |
| 18 | `Tuning _project…` | always | – | Tooltip `What is wrong with this tune, what has been tried, and whether it worked`. Opens/raises the Project window. |
| – | separator | | | |
| 19 | `_Agent API…` | always | – | Tooltip `A local API an AI assistant or a script can watch this session through`. Same toggle as the `AI API` button (no dialog despite the `…`). |
| 20 | `Allow AI _writes` | `AgentIsRunning` | checkable, two-way `AgentWritesArmed` | Tooltip `Lets an agent change settings in the ECU's working memory. It never burns, and this clears itself on disconnect` |

**Logging rate submenu** (and the same list inside the toolbar pop-up's `Logging rate:` submenu):
one checkable item per rate in `[5, 10, 25, 50, 100, 200]` Hz, header `{rate:N0} Hz`, except the
default 25 which reads `25 Hz  (default)` (two spaces). Checked = current `LiveRate`. Click sets
`LiveRate` (persisted by the view model) and closes the menu. Then a separator and the disabled
caption `25 Hz is past what a wideband can resolve; raise it only for transients`.

**Child windows** (Calculators, Insights, Project, Power) are single-instance and owned by the main
window: opening when already open restores it if minimised and brings it to front (Insights also
re-measures; Project does not restore from minimised, it only activates). Calculators/Power/Project
are kept between openings only while open — closing destroys them.

#### Help menu (`_Help`)

| Header | Action |
|---|---|
| `How to use _this app` | Tooltip `The guide, carried inside the application`. Mode = Guide. |
| separator | |
| `_Documentation online` | Opens `https://github.com/TokenGoblin/OpenLogViewer#readme` in the default browser. Failure is only written to the run log: `Could not open {target}: {message}`. |
| separator | |
| `_About OpenLogViewer` | Information box (OK only), title `About OpenLogViewer`, text below. |

About text (`{version}` = assembly version, 3 parts e.g. `0.1.0`; fallback `0.1.0`):
```
OpenLogViewer {version}

Datalog viewer and live tuning for engine ECUs.

Reads MegaSquirt and TunerStudio logs, MaxxECU logs, and delimited text.
Connects live to MegaSquirt, MicroSquirt, rusEFI, Speeduino, MaxxECU,
and any OBD2 vehicle through an ELM327 adapter.

No network code: nothing here is ever sent anywhere.

https://github.com/TokenGoblin/OpenLogViewer
```

---

### The toolbar

A strip below the menu row (background `Panel`, bottom border 1 px `Line`, padding 10,6). Two parts:

**Right group** — always reserved first so it never shrinks; anchored to the top-right; visual order left
to right: `⌸  Project`, `◈  Insights`, `ƒ  Calculators` (glyph, two spaces, word). Each: padding 10,3;
minimum height = the actual height of the Connect button (so they line up); 8 px gap between them.
They stay on the first row when the left group wraps.

| Button | Enabled | Tooltip | Click |
|---|---|---|---|
| `⌸  Project` | always (worth opening with no log) | `What is wrong with this tune, what you changed, whether it worked — and which tune each log came from` | open/raise Project window |
| `◈  Insights` | `HasDocument` | `What this log says about the engine: mixture against target, injector headroom, knock, idle steadiness and more — with the arithmetic behind each` | open Insights window, or if open: restore from minimised, re-measure, bring to front |
| `ƒ  Calculators` | always | `Plan a build, size a turbo, injectors, fuel pump, octane, gearing — none of it needs a log open` | open/raise Calculators window |

**Left group** — the flow panel, filling the rest of the width, in this order:

| # | Item | Visible when | Look | Tooltip | Behaviour |
|---|---|---|---|---|---|
| 1 | `Open log…` button | always | standard button | none | Opens the log dialog. |
| 2 | `Open tune…` button | always | 6 px left margin | `Use tables from a .msq file instead of the one stored in the log` | Left-click: tune dialog. **Right-click**: context menu with one item `Use the tune stored in the log`, enabled when `UsingLoadedTune`; action = clear tune and rebuild analysis. |
| 3 | Reconnect button, text `{ReconnectLabel}` | `CanReconnect` | 6 px left margin | `Reconnect to the ECU you used last  (Ctrl+K)` (two spaces) | Same as Ctrl+K. |
| 4 | Connect button | always | 6 px left margin | `Read live data from an ECU over serial or Bluetooth` | Text: `Connect ▾` normally; `Disconnect` while live; `Scanning…` during a scan. Click while live → Disconnect. Otherwise build the connect list (with session settings) and open it as a pop-up directly below the button. Disabled while a menu-initiated connect probe, a Wi-Fi connect, or a scan is in progress. |
| 5 | Record button, text `{RecordLabel}` | `CanRecord` (= IsLive) | 6 px left margin; while `IsRecording`: text colour `Marker`, semibold | `Start or stop writing the live session to a file of your choosing` | See Recording. |
| 6 | Divider | always | 1 px wide, full height, `Line`, margin 12 left/right, 2 top/bottom | – | – |
| 7 | Mode chips: `Log`, `Gauges`, `Calibration`, `Guide` | always | chip style (below) | `Recordings: the plot and the heat table` / `Live dials, as the firmware defines them` / `Work on the tune itself` / `How to use this application` | radio over Mode |
| 8 | Divider | InLogMode | as 6 | – | – |
| 9 | Log-view chips: `Log`, `Histogram`, `Scatter` | InLogMode | chips | `Time-series view` / `Bin the log into a table, like a tuning table` / `Every sample at its own X and Y, coloured by a third channel` | radio over LogView (note: the Plot chip is labelled `Log`, the menu calls it `Plot`) |
| 10 | Lane chips: `Overlaid`, `Stacked` | ShowLog | 12 px left margin; chips | `All traces share the plot — best for comparing timing between channels` / `Each channel gets its own strip — best when several are plotted` | radio over stacked |
| 11 | Status text `{Status}` | always | margin 16 left/right; colour `Muted`; one line, trimmed with `…` at the end | the full `{Status}` | – (the fill item) |

**Open question:** item 10 is gated by `ShowLog` only, so the Overlaid/Stacked chips stay visible in
Gauges/Calibration/Guide modes (with no divider before them) whenever the log view is Plot; items 8-9
are gated by `InLogMode`. Looks like an oversight; builder should ask the user before changing it.

**Chip style** (`Chip`): hand cursor; rounded (`CornerSmall`), 4 px right margin, padding 8,3, 1 px border
`Line`, transparent background, 11 px text in `Muted`; hover → text `Text`; selected → background
`Selected`, border `Accent`, text `Accent`. Chips in a group are vertically centred.

`Status` texts: at start `Ready — open a .mlg, .msl or .csv datalog`; after a log loads
`{format}   •   {n} channels   •   {samples:N0} samples   •   {duration:F1} s` plus
`   •   {m} markers` if any and `   •   {signature}` if the log has one (separator is three spaces, `•`,
three spaces); live: e.g. `Live — {signature}   •   {n} channels   •   {iniName}`,
`Live — OBD2   •   {n} channels   •   {signature}`, `Live — MaxxECU   •   {n} channels`,
`Live — MaxxECU (USB)   •   {n} channels`, `Live — SSM   •   {n} parameters   •   {adapter}`.

`ReconnectLabel`: `Connect: {name}` where `{name}` = the remembered ECU's signature cut to its first
two space-separated words (whole string if ≤ 2 words), or the port name if no signature; `Connect`
when nothing is remembered. "Remembered ECU" = the known, non-incoming serial port with the most
recent last-used time (undated ones sort last).

#### Wrap rule (ToolbarFlowPanel)

The left group is a custom flow layout whose **last child (the status text) is a fill**:

1. Available width W = width left after the right group (infinite → treat as unbounded).
2. Walk the children in order, keeping a cursor x (start 0), current row top y, current row height h.
3. For every child except the last:
   - Measure at its natural (unconstrained) size.
   - A hidden child (natural width 0) takes no room and **never** starts a new row.
   - If `x > 0` and `x + childWidth > W`, start a new row (y += h, x = 0, h = 0).
   - Place it at (x, y) with its natural width; x += width; h = max(h, child height).
   - A single child wider than W on an empty row is placed anyway (it overflows; it does not wrap).
4. The last child (fill): if no width is left on the current row (`W − x ≤ 0`) and `x > 0`, start a new
   row. Measure it with width `max(0, W − x)`; it occupies from x to the **right end of the row**
   (arranged width = max(its measured width, W − x)), so long status text trims with an ellipsis
   instead of wrapping or overrunning.
5. Panel height = sum of row heights. Width reported = widest row (≤ W).
6. Arranging: if there is only one row, every child gets the **full panel height** (so dividers stretch
   and chips/text centre vertically exactly as in a single-row toolbar). With several rows, each child
   gets its own row's height and sits at that row's top.
7. Nothing groups items together: each control (each chip *group* is one item, since the chips of a group
   are one horizontal strip) moves to the next row independently; items are left-aligned on each row.
   So at a narrow width / large zoom the status text first shrinks, and only once the controls before
   it do not fit do they wrap.

---

### The connect list (toolbar pop-up and Tools ▸ Connect)

Built fresh each time it opens, from (a) the serial ports Windows lists right now with their
descriptions and remembered ECU names (`SerialPortNames.Describe()`), and (b) paired Bluetooth LE
OBD2 adapters (`BleDevices.Obd2Adapters()`). Same code for both places; the toolbar pop-up additionally
appends the session-settings tail.

Port label (`SerialPortInfo.Label`): `{COM} — {remembered ECU}` if something answered there before;
else `{COM} — {device name} (Bluetooth)` if it has a Bluetooth device name; else `{COM} — {description}`;
else `{COM}`. BLE adapter label: `{name} (Bluetooth LE)`.
"Decorate": if anything was learnt about it this run, append ` — {note}` where note is one of
`answered at HH:mm`, `no answer at HH:mm`, `switched on at HH:mm`, `not heard at HH:mm` (local 24 h time
of the attempt). Underscores doubled so they display literally. Notes are kept in memory only.

Order of entries:

1. **Wired ports** (non-Bluetooth), sorted most-recently-used first (by last-used time descending),
   never-used last. If the list contains both remembered and never-answered ports, the disabled
   caption `Used before` comes first and `Other ports` is inserted before the first never-answered port.
   No captions when only one kind exists. Each wired port: no tooltip; click = connect from menu (below).
2. If there are any paired Bluetooth serial ports or BLE adapters:
   - separator (only if there were wired ports),
   - disabled caption `Paired — listed whether or not switched on`,
   - each paired Bluetooth serial port (in Windows' order), tooltip
     `A Bluetooth link. Slower to answer than a cable, so it is given longer to reply and more room to settle between attempts.`,
   - each BLE OBD2 adapter, tooltip `A Bluetooth LE OBD2 adapter. Reads any standard vehicle — no definition file needed.`; click = connect over BLE,
   - `Check which of these are switched on`, tooltip
     `Listens for the LE adapters and tries the others. Takes a few seconds, because a paired port that answers nothing has to be waited out before it can be called dead.`; click = scan (below).
3. If nothing at all was found: disabled `Nothing found`.
4. Always: separator, then `Connect to a Wi-Fi OBD2 adapter` ▸, tooltip
   `For a Vgate iCar Pro Wi-Fi and the dongles built like it. These are their own access point rather than a COM port or a paired device, so join their Wi-Fi first — V-LINK on a Vgate — and pick the address below.`
   Submenu: `192.168.0.10:35000  (Vgate iCar Pro, and most of them)`, `192.168.4.1:35000  (some other clones)`
   (two spaces before the bracket; each decorated with its note), separator, disabled
   `Join the adapter's own Wi-Fi first, and check Windows has stayed on it`.
5. If any serial port exists (wired or Bluetooth):
   - `Connect as an OBD2 adapter` ▸, tooltip `For an ELM327 dongle whose name does not say what it is. Reads any standard OBD2 vehicle — no definition file needed.`; one item per port (label, undecorated); click = connect from menu, forced to OBD2.
   - `Connect over SSM (Subaru)` ▸, tooltip `Subaru's own protocol. Reads knock correction, learnt timing and fuelling trims, which OBD2 does not carry at any speed — about one round a second. Addresses come from ssm-parameters.json in the definitions folder.`; one item per port; click = SSM connect; then separator and `Edit the parameter list…` (tooltip `Which addresses are read, and what they mean`) which opens the definitions folder.
6. **Toolbar pop-up only** (session settings): separator; `Logging rate: {LiveRate:N0} Hz` ▸ (rate list);
   `Units: {UnitsLabel}` ▸ (units list; label `As reported`/`Metric`/`Imperial`); checkable
   `Read the block in one request` (checked = `SingleRequestBlock`; tooltip
   `Faster on a MegaSquirt, which serves its whole block despite declaring a smaller limit. A rusEFI asked for more than it declares stops responding until it is replugged.`);
   `ECU definition files…` (opens definitions folder; tooltip as in Tools menu).

**Not in the list:** MaxxECU over USB (D2XX) and the Holley Sniper probe have **no** menu entry; they are
reachable only by command line (`--connect-maxx-usb`, `--probe-sniper`). A MaxxECU on a COM port
(device name starting `MaxxECU`) appears as an ordinary port and is routed to the MaxxECU protocol
automatically.

#### Connecting from the list

**Serial port (and Connect as OBD2):**
1. Connect button disabled; hint `Connecting to {label}…`.
2. In the background, open and close the port once (twice for Bluetooth). This can take ~20 s for a
   switched-off Bluetooth device; the window must stay responsive.
3. On failure: note `no answer`; warning box `Could not connect on {COM}.\n\n{message}`; hint
   `{COM} did not answer.`; re-enable button; stop.
4. On success note `answered`, re-enable, then connect for real (blocking; show a wait cursor):
   OBD2 if forced or the port's name looks like an OBD2 adapter; else MaxxECU if the device name starts
   `MaxxECU`; else TunerStudio protocol (Bluetooth flag passed). Failure → warning box
   `Could not connect on {COM}.\n\n{message}` (no state change). Success → "session started" (below).
   **Opening a port resets some boards (Speeduino), so any unburned change on them is lost — see safety.**

**BLE adapter:** wait cursor; connect; failure → warning box `Could not connect to {name}.\n\n{message}`.

**Wi-Fi:** Connect button disabled; hint `Connecting to the Wi-Fi adapter at {address}…` (or
`… at the usual addresses…` when none given, which tries both known addresses). Whole conversation in
the background. Failure: note `no answer` against every address tried; warning box
`Could not connect over Wi-Fi.\n\n{message}`; hint `The Wi-Fi adapter did not answer.` Then the
session start (wait cursor); failure → warning box `Could not start the session.\n\n{message}`.
Success → note `answered` against the address actually reached, then session started.

**SSM:** connect; failure (protocol, I/O, permission, invalid operation) → warning box `{message}`.

**Scan** (`Check which of these are switched on`): Connect button disabled, text `Scanning…`. In
parallel: listen 3 s for BLE advertisements, and try to open each paired Bluetooth serial port.
Ports → note `answered` / `no answer`; BLE adapters → `switched on` if heard else `not heard`. A failure of the
scan itself is only written to the run log (`Could not scan for devices: {exception}`). Then the button
returns to `Connect ▾`, enabled, and the toolbar pop-up is re-opened so the new notes show (also
when the scan was started from the Tools menu).

**Session started** (every successful path): Connect button text `Disconnect`; plot follows the newest
data; a 200 ms refresh timer (any previous one stopped first) pulls new data into plot/gauges; while the
log view is not Plot, the histogram/scatter is rebuilt every 5th tick (1 s). If a tick finds the session
ended (fault), it disconnects.

**Disconnect** (Connect button while live, Tools ▸ Disconnect, window close, session fault): stop the
timer, stop the session and close the port, disarm agent writes, Connect button text `Connect ▾`. If a
document is on screen, hint `Disconnected. The session is still here, and its recording is on disk.`
A faulted session sets the hint to its error text instead.

#### Ctrl+K / Reconnect

Connects to `LastConnected` exactly as `--connect` does (direct, without the background probe), as OBD2
if that port is an OBD2 adapter. Does nothing when nothing is remembered.
**Open question / likely bug (safety-relevant):** Ctrl+K does **not** check `CanReconnect`, so pressing it
while already live starts a second connection on the remembered port (the toolbar button and menu item are
hidden/disabled while live, Ctrl+K is not). Recommend: Ctrl+K does nothing while live. User decision.

---

### Recording (toolbar Record button / Tools menu)

Only possible while live. If recording: stop, Report(`StopRecording()` result). Otherwise a Save dialog:
title `Record the live session to`, filter `CSV log|*.csv|All files|*.*`, default ext `.csv`, overwrite
prompt, suggested file name and folder from `SuggestedRecordingPath()` =
`{folder}\{ecu}-{now:yyyy-MM-dd_HH-mm-ss}.csv` (folder = last chosen recording folder if it still exists,
else workspace Logs; ecu = live signature cleaned for the file system, or `live`). On OK:
Report(`StartRecording(path)`). Cancel: nothing.

---

### The status bar

Bottom strip (background `Chrome`, top border 1 px `Line`, padding 12,5). All text 11 px.

| Position | Element | Visible | Colour | Tooltip | Content |
|---|---|---|---|---|---|
| far left | Live status | `IsLive` | `Accent`; `Marker` when `LiveHealthy` is false (link down, waiting) | `LiveDetail` (multi-line, below) | `LiveStatus`; max width 320, 14 px gap after, trimmed with `…` |
| fill (middle, left-aligned) | Hint | always | `Muted` | the full hint | `Hint`, one line, trimmed |
| right, third from right edge | AI activity | `AgentIsRunning` | dot `●` in `Accent` (`Marker` while writing), text `Muted` (`Marker` while writing) | `AgentLastAction` | dot (5 px gap) + `AgentLastAction` (max width 220, trimmed); 14 px left margin |
| right, second | Cursor time | always | `Accent`, font `DataFont` | – | `CursorTime`; 14 px left margin |
| right, rightmost | Tune | always | `Faint`; `Accent` when `UsingLoadedTune`; `Marker` when `HasTuneWarning` (wins) | `TuneDetail` | `Tune: {TuneSource}`; max width 200, trimmed; 14 px left margin |

Dot animation: resting opacity 0.35. While `AgentIsActive` (an agent request in the last 1.5 s) the
opacity pulses 0.35 → 1.0 over 0.6 s and back, repeating; when activity ends the pulse stops and it
returns to 0.35.

`LiveStatus` while live (refreshed every tick):
- faulted: the error text;
- link down: `○ {port} · waiting for the ECU to come back…`;
- otherwise: `● {port} · {signature} · {rate:F1} Hz` + (` · {retries} retries` if retries > 0) + ` · {written}`
  (`{written}` describes what has been recorded, from the view model).

`LiveDetail` (tooltip), lines joined by newline, label column padded with spaces:
```
Port      {port}
Firmware  {signature}
Build     {version}
INI       {ini path}
Channels  {n}[  (+{k} not decoded)]
Recording {path or "not recording"}
```
or `Not connected.` when not live.

`CursorTime`: `—` (em dash) at start and when a span is cleared; `{t:F3} s   (sample {i:N0})` while the
plot cursor is on a sample; `{dt:F3} s selected   ({n:N0} samples)` while a span is marked.

`TuneSource`: name of the opened .msq if one is in use; else `from the log` if the log embeds a tune;
else `{format}, unreadable` if it embeds one in a format this cannot read; else `none`.
`TuneDetail`:
- opened .msq: `Tables come from {name}. Right-click to go back to the log's own tune.`
- embedded: `Tables come from the tune stored in this log — the one that was running when it was recorded.`
- unreadable: `This log carries its {format} tune, in a format this cannot read. Axis breakpoints and VE Calibration need a tune it can, so both are unavailable for this log.`
- none: `This log carries no tune. Open a .msq to bin onto its table axes and to use VE Calibration.`

**Open question:** the tune tooltip says "Right-click to go back", but the status-bar text has no
right-click menu; the right-click lives on the toolbar's `Open tune…` button. Builder may add the same
context menu to the status-bar tune text (harmless) or keep C# behaviour.

Default hint (`DefaultHint`, also restored by `ResetHint`):
`Scroll = zoom  •  Drag = pan  •  Double-click = fit  •  Hover a trace for its min/max, then click either to jump there  •  Shift-drag to mark a span and summarise it  •  Right-click for more`
(two spaces either side of each `•`).

---

### Interface zoom (View ▸ Zoom)

- Steps: **1.0, 1.15, 1.25, 1.5, 1.75, 2.0** (labels 100%-200%). The current scale is always one of them.
- `Snap(v)` = the step nearest v (non-finite → 1.0). `Larger(v)` = first step greater than Snap(v),
  else 2.0. `Smaller(v)` = last step less than Snap(v), else 1.0.
- **Keys** (work wherever keyboard focus is, including inside text boxes and the tune table, and are
  consumed so the focused control never sees them; only when Ctrl is the *only* modifier held):
  `Ctrl` + `=`/`+` key (main row) or numpad `+` → larger; `Ctrl` + `-` (main row) or numpad `-` → smaller;
  `Ctrl` + `0` (main row) or numpad `0` → 100%. They take precedence over the tune table's own +/− nudge keys.
- Setting a new scale (menu or keys) applies it immediately to every window and **persists** it
  (settings file `UiScale`; stored only when ≠ 1; on load a stored value outside 1-3 is ignored → 1, and
  any value is snapped to a step). Choosing the current step is a no-op.
- `--zoom <number>` (invariant-culture number, e.g. `1.5`) applies a snapped scale for this run only,
  not persisted; applied before the window is shown.
- **What it scales:** the whole content of every window (main window, Insights, Calculators, Project,
  Power, Faults, dialogs owned by the app) **and** every pop-up — drop-down menus, context menus,
  tooltips, combo-box lists — as one uniform layout scale: text, padding, fixed widths, borders, chips,
  self-drawn views all grow together and are re-laid-out (not bitmap-stretched; stays sharp).
  The window's own frame/size does not change (min size stays 760x420 logical); content simply needs
  more room, which is why the toolbar wraps and menus scroll.
- **Why not font size alone:** the UI has ~200 fixed text sizes and as many fixed widths; enlarging text
  alone would overflow the buttons and rows it sits in. A uniform zoom keeps every proportion.
- egui note for builders: this maps to a global pixels-per-point multiplier applied to all viewports,
  not a font-size change.

---

### Global keyboard shortcuts

| Keys | Where | Action |
|---|---|---|
| Ctrl+O | anywhere in the main window | Open log dialog |
| Ctrl+K | anywhere | Reconnect to last ECU (see open question re live) |
| Ctrl+F | anywhere, only when log view is Plot (`ShowLog`) | **Toggles** the find bar: opens and focuses its box, or closes it and clears the shaded findings. (The menu item only opens/focuses.) Note: works in any mode while log view is Plot. |
| Ctrl + `+`/`=`, Ctrl + `-`, Ctrl + `0` (main row or numpad) | anywhere, pre-empts focused controls | Zoom in / out / 100% |
| Alt + underlined letter | menu bar | Opens that menu / item (standard access keys as listed in the menu tables) |

Keys belonging to mode screens (find bar Enter/Shift+Enter/Esc, preset name Enter/Esc, set-value Enter,
tune table + − PgUp PgDn Esc) are specified by those sections. There are no F-key shortcuts, no
Ctrl+W/Ctrl+Q, and Esc does nothing at window level.

---

### Drag-and-drop

- Dragging over the window: shows the "copy" cursor if the drag carries files, otherwise "not allowed".
- Dropping: only the **first** file is used; others are ignored silently.
  - Extension `.msq` (case-insensitive) → load it as the tune (same as Open tune…).
  - Anything else → load it as a log (content is sniffed, extension does not matter).
- Mode is not changed by a drop.
- Errors: log → warning box `Could not open {fileName}.\n\n{message}`; tune → warning box
  `Could not read {fileName}.\n\n{message}` (file-format, I/O or permission errors).

#### Opening a log

Dialog title `Open datalog`, file must exist, filter (in order):
`All datalogs` (`*.mlg;*.msl;*.csv;*.txt;*.log;*.tsv;*.dat;*.MaxxECU-Zip-log`),
`Binary log — MegaSquirt / rusEFI (*.mlg)`, `TunerStudio text log (*.msl)`,
`MaxxECU log (*.MaxxECU-Zip-log)`, `Delimited text — CSV / TSV (*.csv;*.tsv;*.txt;*.log)`, `All files`.
On OK the log loads (title, status, default channels RPM/MAP/TPS/AFR/CLT ticked — see Log section).

#### Opening a tune

Dialog title `Open tune`, filter `TunerStudio tune|*.msq|All files|*.*`, must exist. On OK load it and
rebuild histogram/scatter.

---

### Restoring a tune to the ECU (Tools menu) — safety

1. Open dialog title `Restore a saved tune to the ECU`, filter `TunerStudio tune|*.msq|All files|*.*`.
2. Report(`PlanRestore(path)` result). If no plan resulted, stop. If the plan is empty (nothing differs),
   cancel it and stop (no box).
3. Confirmation box, title `OpenLogViewer`, warning icon, buttons **OK / Cancel**, default **Cancel**:
```
Restore {fileName} to the ECU?

{N:N0} settings would change:
    {summary 1}
    …(up to 12 lines, each indented 4 spaces)
    …and {N-12:N0} more            ← only if N > 12

{bytes:N0} bytes across {pages} page(s).

{M:N0} settings this firmware has are not in the file. They are left exactly as the ECU holds them.   ← only if M > 0

THIS FILE IS FOR ANOTHER FIRMWARE.
The file says "{fileSignature}" and the ECU says "{ecuSignature}". Settings may not mean the same thing in both.   ← only if signatures differ

Nothing is burned, so a power cycle undoes all of it.
Do this with the engine stopped.
```
   Exact assembly: `Restore {file} to the ECU?\n\n{N} settings would change:\n{listed}\n\n{bytes} bytes across {pages} page(s).` + (`\n\n{M} settings this firmware has are not in the file. They are left exactly as the ECU holds them.` if M>0) + (`\n\nTHIS FILE IS FOR ANOTHER FIRMWARE.\nThe file says "{f}" and the ECU says "{e}". Settings may not mean the same thing in both.` if signatures disagree) + `\n\nNothing is burned, so a power cycle undoes all of it.\nDo this with the engine stopped.`
4. Cancel → cancel the plan. OK → Report(`ApplyRestore()` result). No burn happens here.
5. There is deliberately **no** command-line way to apply a restore (`--plan-restore` only plans, then cancels).

---

### Guide mode

Visible when Mode = Guide; fills the mode area. Three columns:

| Column | Width | Content |
|---|---|---|
| Left | 230 px (min 160), resizable | background `Panel`, 1 px right border `Line` |
| Splitter | 4 px | draggable, colour `Line`; resizes the two columns |
| Right | rest (min 320) | the reading pane |

**Left column**, top to bottom:
- Search box: margin 8 left/right, 8 top, 6 bottom; tooltip `Search every section`; updates
  `GuideSearch` 150 ms after the last keystroke. No placeholder text.
- Section list: transparent, no border, no horizontal scroll; one row per section showing its title in
  `Text`, padding 2,3, wrapping. Clicking a row selects that section (`GuideSection`). The first section
  is selected at start. The list selection stays highlighted even while a search is showing.

**Right pane**: vertical scroll when needed, no horizontal scroll, 4 px right padding. Content column
margin 22 left, 18 top, 22 right, 26 bottom; **max width 760**, left-aligned (prose line length cap).
- Heading `GuideHeading`: 19 px semibold `Text`, wraps.
- Blurb `GuideBlurb`: 12 px `Muted`, 4 px above, 16 px below, wraps.
- Entries `GuideEntries`, each a block with 18 px below it:
  - A wrapping row: entry title (13 px semibold `Accent`), then — only if the entry has keys — a
    **shortcut chip** immediately after the title (9 px left gap, 1 px top offset, vertically centred):
    rounded `CornerSmall`, padding 6,1, background `Bg`, 1 px border `Line`, text = the keys string in
    10 px `DataFont` `Muted`. If the title is long the chip wraps to the next line rather than squeezing.
    3 px below the row.
  - Body: 12.5 px `Text`, wrapping, line height 19 px.

**Rules:**
- Not searching (search text trimmed is empty): heading = selected section's title; blurb = its blurb;
  entries = its entries. (If no section is selected, the first section is used.)
- Searching: entries = every entry in every section whose title, body or keys contain the trimmed text
  (case-insensitive substring), in section order. Heading `1 result` or `{n:N0} results`
  (`0 results` included). Blurb: none found → `Nothing here mentions that. Try a shorter word — the search covers every section.`;
  else `Mentioning “{text}”, across every section.` (curly quotes U+201C/U+201D).
- Selecting a section while search text is present does not clear the search; the search wins.
- Content (sections, blurbs, entries, keys) is static data from core `Guide.Sections`; port it verbatim.
  Current sections in order: Getting started; Reading the plot; Finding a moment; The heat table;
  Scatter; Insights; The tuning project; Tune versions; Letting an assistant help; VE Calibration;
  Calculated channels; Colours and scales; Live connection; Gauges; Editing a tune; Fault codes;
  Calculators; Export; Appearance; Where your files are. Example key chips: `Ctrl+O`, `Shift-drag`,
  `Ctrl+F`, `Click a cell`, `Click a mark`, `Find it`, `Right-click ▸ Smoothing`, `Right-click a row`,
  `Ctrl+K`, `+ − PgUp PgDn Esc`.
- `--guide <text>`: switches to Guide; if a section title contains the text (case-insensitive) select the
  first such section; otherwise put the text in the search.

---

### The Insights window

Separate window titled `Insights`, 880 wide x 720 tall, centred on the main window, owned by it
(closes with it, stays above it), background `Bg`, single instance. Opened by the toolbar `◈  Insights`
button (enabled when a document exists) or `--insights`. Measures `MainViewModel.Document`.

Layout (top to bottom):
1. **Header** — background `Panel`, 1 px bottom border `Line`, padding 16,12. `Heading` (16 px semibold
   `Text`, wraps) and below it, 4 px gap, `Subheading` (11 px `Muted`, wraps).
2. **Findings** — scrolling list (vertical scroll when needed), padding 16,12. Each finding is a card:
   10 px gap below, padding 12,10, rounded `Corner`, background `PanelAlt`, a **3 px left border** in the
   finding's level colour. Inside, stacked:
   - Row: badge (10 px **bold**, level colour, 8 px right gap) then topic (10 px `Muted`).
   - Title: 13 px semibold `Text`, 5 px above, wraps.
   - Detail: 12 px `Text`, 4 px above, wraps, line height 18.
   - Evidence (the arithmetic): 10 px `DataFont` `Muted`, 6 px above, wraps; only when non-empty.
3. **Footer** — background `Panel`, 1 px top border `Line`, padding 16,10. Left: `Copy all` button
   (padding 12,4, 8 px right gap, tooltip `Puts every finding on the clipboard, numbers included`) and
   `Re-run` (padding 12,4, tooltip `Measures the log again, which matters on a live session`). Right:
   `Tally`, 11 px `Muted`, vertically centred.

Levels (badge text / colour token), findings arrive ordered worst first from core `LogInsights.From(log)`:

| Level | Badge | Colour |
|---|---|---|
| Warning | `WARNING` | `Danger` |
| Watch | `WATCH` | `Warning` |
| Note | `NOTE` | `Accent` |
| Good | `GOOD` | `Nominal` |
| Unanswered | `NOT MEASURED` | `Muted` |

Cards are read-only: no click, hover, selection or context menu.

**States and texts**
- No document or 0 samples: no findings; Heading `No log is open.`; Subheading
  `Open a datalog, or connect to an ECU and record one, and every finding below is measured from it.`;
  Tally empty.
- While the first measurement is running: whatever was shown before (initially empty strings, no cards).
- After measuring:
  - Heading: W warnings > 0 → `{W} thing{s} worth stopping for.`; else watch count V > 0 →
    `Nothing dangerous, {V} thing{s} worth a look.`; else `Nothing here looks wrong.` (`{s}` = `s` unless count is 1).
  - Subheading: (`{fileName} — ` if the log has a file name) +
    `measured from {samples:N0} samples over {minutes:0.#} minutes. Every finding says what it rests on; where a log cannot answer something, it says that rather than guessing.`
    (`minutes` = (last time − first time)/60; 0 if fewer than 2 samples).
  - Tally: parts joined with ` · ` (space, middle dot, space), empty parts dropped:
    `{W} warning{s}` (only if W>0), `{V} to watch` (only if V>0), `{notes} notes`, `{good} good`,
    `{unanswered} not measured` (the last three always, even when 0, always plural).
- Measuring runs off the UI thread; only the newest run's result is applied (older results are
  discarded). A failed measurement leaves the screen unchanged, silently.

**Re-measuring:** on `Re-run`; when the toolbar button is clicked while open; and automatically whenever
the main document changes (new log, or each live snapshot) — throttled to at most once per 5 s: if 5 s
have passed since the last applied measurement, measure now; else schedule one measurement for when 5 s
will have passed (further changes meanwhile do nothing extra).

**Copy all** puts plain text on the clipboard:
```
{Heading}
{Subheading}

[{BADGE}] {Topic}: {Title}
    {Detail}
    ({Evidence})        ← only when evidence is non-empty

…next finding…
```
(blank line after each finding). Success writes `{n} findings copied.` to the run log; if the clipboard is
busy, writes `The clipboard was busy — nothing was copied.` to the run log. **Open question:** neither
message is shown to the user (no hint in this window). Builder may show a transient note; user decides.

The level colours are taken when the cards are built; a theme change recolours them on the next measurement.

---

### Closing the window

- There is **no confirmation prompt** of any kind on close (File ▸ Exit, title-bar ×, Alt+F4) — not for
  a live session, an active recording, unsent table/setting edits, or unburned writes.
- On close: the live session is stopped (timer stopped, port closed, recording file closed), which
  disarms agent writes. Owned windows (Insights, Calculators, Project, Power, Faults) close with it and
  the application exits.
- The agent API is not explicitly stopped; it ends with the process (its `agent-api.json` is left in the
  data folder). **Open question:** builder may stop the API (and delete the file) on close; harmless.
- **Open question (user decision):** whether closing with unsent edits, unburned ECU writes or an active
  recording should prompt. C# does not.

---

### First-run state (nothing loaded, nothing connected, no settings file)

- Title `OpenLogViewer`; window at fitted design size, centred; theme `Midnight`; zoom 100%.
- Mode Log, log view Plot, overlaid traces; hold-steady on; units As reported; logging rate 25 Hz;
  record-on-connect on; single-request off.
- Menu row: light `Accent`, button `AI API: On` (the agent API starts on port 8765 at every launch,
  read-only), `Allow AI writes` enabled but not pressed.
- Toolbar: `Open log…`, `Open tune…`, `Connect ▾` (no Reconnect button — nothing remembered; no Record
  button), divider, mode chips with `Log` selected, divider, `Log`/`Histogram`/`Scatter` with `Log`
  selected, `Overlaid` selected, status text `Ready — open a .mlg, .msl or .csv datalog`; right group
  `⌸  Project`, `◈  Insights` (disabled), `ƒ  Calculators`.
- Status bar: no live text; hint = the agent start message (`The agent API is listening on http://127.0.0.1:8765. Its token is in {dataFolder}\agent-api.json.`)
  — it replaces the default hint because the start is Reported; the AI indicator visible with an empty
  action text and a dim dot; cursor time `—`; `Tune: none` in `Faint`.
- File ▸ Compare/Export/Use the tune stored… disabled; Tools ▸ Disconnect, Fault codes, Record, Save tune,
  Compare tune, Restore, Estimate power disabled.
- Persisted across runs (settings file in `%APPDATA%\OpenLogViewer\settings.json`, owned by the core):
  theme id, zoom, units, logging rate, record-on-connect, single-request, data folder, remembered ECUs.
  **Not** persisted: window geometry, mode, log view, lane style, hold-steady, agent write arming.

---

### Command line

`OpenLogViewer.exe [log-file] [switches…]`. Switch names are case-sensitive (exact match) except when
locating the log path. Each switch is looked up by its first occurrence. Values are the next token.

**Finding the log file:** walk the arguments; a token starting `--` is a switch — if it is in the
"takes a value" list (below) skip the next token too; the first token that is neither is the log path.
"Takes a value" list: `--theme --screenshot --export --connect --connect-ble --connect-menu --settle
--menu --scan-menu --top-menu --calculators --power --calibration --cell --tune-cell --select --compare
--z --tune-axes --pointer --mark --find --guide --settings --page --live-page --insights --open-tune
--save-tune --compare-tune --plan-restore --faults --connect-ssm --connect-wifi --agent-api
--tuning-project --connect-maxx-usb --probe-sniper --zoom` (this comparison is case-insensitive).

**Open questions (bugs a builder can fix, or keep for oracle parity — ask the user):**
- `--top-menu` and `--calculators` take **two** values but only one is skipped, so the second (the
  output .png) is taken as the log path and the app tries to open it as a log (error box if it does not
  exist / is not a log), before the capture runs.
- `--insights` and `--probe-sniper` are flags in practice but are in the value list, so a log path
  written right after them is swallowed and not opened. Likewise the optional-value switches
  (`--calibration`, `--guide`, `--live-page`, `--tuning-project`, `--connect-maxx-usb`) swallow a log path
  that follows them when used without their value.

**Start-up order** (each step only if its switch is present; this order is what the oracle screenshots rely on):

| Step | Switch | Effect |
|---|---|---|
| 1 | `--theme <id>` | apply theme for this run only (not saved), before the window shows |
| 2 | `--zoom <n>` | apply zoom (invariant number, snapped) for this run only, before the window shows |
| 3 | – | window shown |
| 4 | `<log-file>` | load the log (error box on failure) |
| 5 | `--connect <COM>` [+ `--obd2`] | direct connect on that port (OBD2 forced with `--obd2`); failures go to the run log only, no box; run log gets `connecting on {port}…` then `live: {Status}` or `not live` |
| 6 | `--tuning-project [vehicle]` | if a vehicle (next token not starting `--`) open it (Report result); open the Project window; Report `project window opened: {ProjectSummary}` |
| 7 | `--agent-api [port]` | **always** runs: start the agent API on the given positive integer port, else 8765; read-only; Report result. There is no switch that arms writes. |
| 8 | `--connect-ssm <COM>` | SSM connect (warning box on failure) |
| 9 | `--connect-maxx-usb [serial]` | MaxxECU over USB/D2XX. With no serial and not exactly one device found → warning box: 0 found `No MaxxECU was found over the D2XX driver. Check it is plugged in, powered, and that the FTDI driver is installed.`; several `{n} MaxxECU devices were found over the D2XX driver. Pass a serial number to choose one: {serial1, serial2…}` (joined `, `). Connect with wait cursor; failure → disconnect, warning box `{message}`. |
| 10 | `--probe-sniper` | Holley Sniper diagnostic probe (not a connect), wait cursor, Report(outcome) |
| 11 | `--connect-ble <name>` | first paired BLE device whose name contains the text; run log `connecting over Bluetooth LE to {name}…`, `no paired Bluetooth LE device matching "{name}"` if none, then `live: …`/`not live`; errors to run log only |
| 12 | `--connect-wifi <addr\|auto>` | Wi-Fi OBD2 (`auto` = try known addresses); quiet (errors to run log); not awaited |
| 13 | `--connect-menu <COM>` | connect via the menu path (background probe first); run log `connecting via the menu on {port}…`, then `live: {Status}` or `not live: {Hint}`; not awaited |
| 14 | `--stacked` | stacked lanes |
| 15 | `--gauges` | Mode = Gauges |
| 16 | `--calibration [table]` | Mode = Calibration; with a table: type it into the calibration table search box, select first table whose name contains it |
| 17 | `--scan-menu <out.png>` | scan paired devices, render the toolbar connect pop-up to PNG, **exit** (later steps skipped) |
| 18 | `--top-menu <Header> <out.png>` | open the named menu-bar menu (`File`/`View`/`Tools`/`Help`, underscores ignored, case-insensitive), render its drop-down to PNG, exit. Unknown → run log `no menu called "{header}"`. |
| 19 | `--calculators <Tab> <out.png>` | open Calculators on that tab, render, close, exit. Unknown tab → run log `no calculator called '{tab}'` (still renders) |
| 20 | `--power <out.png>` | open Power estimate, render, close, exit |
| 21 | `--faults <out.png>` | open Fault codes, render, close, exit |
| 22 | `--menu <out.png>` | open the toolbar connect pop-up, render it, close, exit |
| 23 | `--cell <col,row>` | rebuild analysis and trace that heat-table cell to the log |
| 24 | `--tune-cell <col,row[,nudge]>` | select that tune-table cell and nudge it locally by `nudge` (invariant number; 0 = select only). Never sent or burned. |
| 25 | `--select <from,to>` | mark a span in seconds (current-culture numbers) |
| 26 | `--histogram` (+ `--tune-axes <i>`, `--count-colour`, `--count-value`, `--compare <channel>`, `--z <channel>`, `--ve`, `--ve-values`) | log view Histogram; Z channel by exact name (case-insensitive); axis source by index (>0 and in range); compare target channel; colour by count; count statistic; `--ve` enables VE Calibration, `--ve-values` shows suggested values |
| 27 | `--scatter` (+ `--count-colour`, `--compare`, `--z`, `--mark <col,row>`) | log view Scatter, same options; `--mark` traces a block back to the log |
| 28 | `--find "<condition>"` | open the find bar with that condition, run it, frame the first hit |
| 29 | `--guide [section or text]` | as described in Guide mode |
| 30 | `--settings <ini> [--page <text>]` | Calibration mode, open the definition with no ECU, show settings pages, open first page whose title contains the text (else first page) |
| 31 | `--live-page [text]` | Calibration mode, settings pages of the tune in hand, page by title as above |
| 32 | `--open-tune <msq> [--page <text>]` | Calibration mode, open saved tune; failure Reports `EcuTuneSummary`; else pages as above, then Report summary |
| 33 | `--compare-tune <msq>` | Report(compare result) |
| 34 | `--plan-restore <msq>` | Report(plan), then cancel the plan — never applies |
| 35 | `--save-tune <msq>` | write the tune in hand to that file, Report result |
| 36 | `--insights` | open the Insights window |
| 37 | `--screenshot <out.png>` | after layout settles, render to PNG and exit: if an owned window is visible, the last-opened visible one is captured instead of the main window. Before rendering: `--pointer <fx,fy>` moves the plot cursor to that fraction of the plot (current-culture numbers); `--ecu-ve` picks the axis source whose label contains `VE Table` and `from the ECU` (run log `using the ECU's fuel table` / `no ECU fuel table offered`) and then re-applies `--ve`/`--ve-values`. Rendered at the screen's DPI. |
| 38 | `--export <folder>` | after layout, create the folder and write every export of the current log view without dialogs (Histogram: table.csv, counts.csv, table.png; Scatter: points.csv, scatter.png; else plotted.csv, channels.csv, plot.png; names `{stem}-{suffix}.{ext}`), then exit |

**Scripted-exit timing:** capture/export steps run once the window is idle after layout, or — when
`--settle <ms>` (positive integer) is given — after that many milliseconds (needed for live sessions,
which have nothing to draw until data arrives). Success → exit code 0; any exception → run log
`scripted run failed: {exception}` and exit code 1. Async captures (scan) are awaited before exiting.
If both `--screenshot` and `--export` are given, both are scheduled.

**Run log:** `%TEMP%\openlogviewer-run.log`, appended, each line `{HH:mm:ss}  {message}` (two spaces).
Failures to write are ignored. Unhandled exceptions are appended as
`unhandled on a background thread: {exception}` / `unhandled on the UI thread: {exception}`.

---

### Open questions

1. Title keeps `Live: …` after Disconnect (builder can settle with user; C# = unchanged).
2. AI API toggle label is the current state although the code comment says it is the action (show C# text).
3. Menu gesture texts (Ctrl+O/F/K) are declared but never drawn by the menu template (builder decides whether to draw a gesture column; C# = not drawn).
4. View ▸ Overlaid/Stacked/Find/Reset zoom enabled by `ShowLog` even outside Log mode (likely oversight; user decision to change).
5. Toolbar Overlaid/Stacked chips visible outside Log mode when log view is Plot (likely oversight; user decision).
6. Ctrl+F works in any mode while log view is Plot (opens a find bar that is not on screen) — same root as 4.
7. Ctrl+K reconnects even while live, starting a second connection (bug; safety-relevant; user decision — recommend no-op while live).
8. Status-bar tune tooltip says "Right-click to go back" but the status text has no context menu (builder can add it).
9. Insights `Copy all` success/failure messages go only to the run log, never shown (user decision).
10. Command-line parsing: `--top-menu`/`--calculators` second value taken as a log path; `--insights`/`--probe-sniper` and optional-value switches swallow a following log path (bugs; keep for oracle parity or fix — user decision).
11. No close prompt for live session/recording/unsent or unburned edits (user decision).
12. Agent API not stopped and `agent-api.json` left behind on close (builder can settle).
13. Selecting the already-active theme/zoom step after a `--theme`/`--zoom` preview does not persist it (builder can settle; minor).

### Safety rules found

- **Agent writes**: off at every start, never persisted, no command-line switch can arm them; armed only by the `Allow AI writes` toggle (menu row or Tools menu), enabled only while the API runs; disarmed on every Disconnect, on API stop and on window close. Hints: `An agent may now change settings and table cells in the ECU's working memory. Nothing it does is burned, and this turns itself off when you disconnect.` / `Agent writes are off. The API can still read everything.`
- **Agent API starts at every launch** (port 8765 or `--agent-api <port>`), read-only, token written to `{dataFolder}\agent-api.json`; one click on `AI API: On` stops it.
- **Restore a saved tune** (Tools, enabled by `CanBurn`): OK/Cancel box defaulting to Cancel, listing up to 12 settings, byte/page counts, untouched-settings note, `THIS FILE IS FOR ANOTHER FIRMWARE.` warning on signature mismatch, ending `Nothing is burned, so a power cycle undoes all of it.\nDo this with the engine stopped.` Never burns. `--plan-restore` only plans; no switch applies a restore.
- **Connecting opens the port**, which resets some boards (Speeduino) and loses unburned changes; launching never connects on its own (no auto-reconnect); Reconnect is one explicit click/Ctrl+K. Ctrl+K while live reconnects anyway (open question 7).
- **Calibration writes/burns** live in this file but belong to the Calibration section (all OK/Cancel, default Cancel, title `OpenLogViewer`): `Send {n} changed cell{s} of {table} to the ECU?\n\nThis takes effect immediately on a running engine.\n\nIt is not permanent: the ECU forgets it at the next power cycle unless you burn it.`; `Burn the page holding {table} to the ECU's flash?\n\nThis is permanent. A power cycle will not undo it.\n\nBurn with the engine stopped: the ECU pauses while it writes flash.`; `Send {n} changed setting{s} to the ECU?\n\n{bytes:N0} bytes across {pages} page{s}.\n\nThis takes effect immediately on a running engine.\n\nIt is not permanent: the ECU forgets it at the next power cycle unless you burn it.`; `Send {n} moved point{s}{ of "{curve}"} to the ECU?\n\nIt takes effect at once. Nothing is burned, so a power cycle undoes it.` (curve name only when one curve is open); `Burn {pages} page{s} of settings to the ECU's flash?\n\nThis is permanent. A power cycle will not undo it.\n\nBurn with the engine stopped: the ECU pauses while it writes flash.`
- **`--tune-cell`** edits only the on-screen copy; nothing is sent or burned by any command-line switch.
- **Closing the window** disconnects (port closed, recording closed) with no prompt, discarding unsent edits silently (open question 11).
