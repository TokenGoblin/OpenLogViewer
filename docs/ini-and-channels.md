# Firmware definitions, channels and roles

How OpenLogViewer reads a TunerStudio-style INI, what it takes out of one, and why a
channel that plainly exists can still go unrecognised.

This is the part of the application with the least room to guess. A definition file is
the only thing that says what the bytes coming off a controller mean, and getting it
subtly wrong does not throw — it produces numbers that look entirely reasonable.

---

## Finding the file

An ECU is asked several questions about itself on connecting. The answers are candidate
identity strings; the **signature** is whichever one a definition file on disk
recognises, and the rest are build strings. `IniCatalog.MatchAny` does that matching.

Searched in order, ours first:

| Where | Why |
|---|---|
| `<workspace>/ECU definitions` | Somewhere obvious to put one, for a firmware too new for the copy on disk, or a machine with no TunerStudio |
| `~/.efiAnalytics/TunerStudio/config/ecuDef` | Where TunerStudio keeps firmware definitions |
| `~/Documents/TunerStudioProjects` | Each project carries its own `projectCfg/mainController.ini` |
| `~/OneDrive/Documents/TunerStudioProjects` | The same, redirected |

The workspace folder is deliberately first: a file somebody went to the trouble of
putting there is a more deliberate answer than one a tool cached at some point.

`IniCatalog.Scan` reads only the first 400 lines of each file looking for a signature,
and skips files that have none — a projects directory is full of INIs that are not
firmware definitions.

**A session is refused when nothing matches, and adjacent versions count as no match.**
Firmware versions move channels around inside the realtime block. Decoding with the
wrong INI does not fail; it reads every channel from the wrong offset. Refusing is the
only safe answer, and the folder note names the signature the ECU actually reported,
which is the one piece of information that makes finding the right file possible.

An INI that lives inside a TunerStudio *project* has a tune two directories up from it.
`IniCatalog.ProjectTuneFor` finds it, because some of what a gauge needs is kept nowhere
else: a MegaSquirt tachometer runs to `{rpmhigh}` and warns at `{rpmwarn}`, and those are
TunerStudio's variables rather than the firmware's.

## Reading the file

**INIs are ISO-8859-1.** An MSQ tune says so in its XML declaration; an INI says nothing
and is written that way regardless. `TuningText.Decode` tries strict UTF-8 first, since a
modern file may well be, and falls back to Latin-1 on invalid bytes. A BOM is stripped.

Read as UTF-8 they *mostly* work, because most of the content is ASCII — and then the
degree sign, the one byte that matters in a units string, decodes to a replacement
character and every temperature channel is labelled `?F`. That is not cosmetic: units
decide whether a channel can fill a role (below), so a mangled degree sign silently costs
you coolant and intake air temperature on every MegaSquirt.

Use `TuningText.Read`, never `File.ReadAllText`.

## What a firmware calls its channels — twice

**This is the part that catches people, including this application.**

An INI names every channel **two** different ways, and they are not alike:

| | Where | Example on a rusEFI |
|---|---|---|
| **Field names** | `[OutputChannels]`, via `MsqIni.ReadOutputChannels` → `RealtimeDecoder.Names` | `RPMValue`, `coolant`, `correctedIgnitionAdvance`, `veValue` |
| **Datalog labels** | `[Datalog]`, via `MsqIni.ReadDatalog` → `DatalogEntry.Label` | `RPM`, `CLT`, `Timing: ignition`, `Fuel: VE` |

**A live session and anything recorded from one carry the labels.** `LiveSession` is
built on a `TunerStudioSource`, which walks the datalog entries, finds each one's field in
the decoder, and names the channel by its label — falling back to the field name only
where the label is empty, and dropping duplicates.

Two consequences:

- **Only logged channels appear.** The datalog section is a subset, sometimes a small one.
- **The names are not the field names.** Anything matching on `RPMValue` finds nothing in
  a live session, and anything matching on `RPM` finds nothing in a definition file.

Measured on the definitions to hand:

| Firmware | Published | Logged |
|---|---|---|
| Speeduino 202501 | 181 | 81 |
| MS2Extra 3.4.2h2 | 162 | 96 |
| MS3 0592.13P | 513 | 390 |
| rusEFI 2024.11.17 uaefi | 880 | 823 |
| rusEFI 2026.09.03 super-uaefi | 1030 | 1028 |

**Both sets occur in the wild**, which is why the alias tables have to carry both: a
controller's own logs tend to use field names — a rusEFI writes `RPMValue` — while
anything recorded through this application uses labels.

### Expressions, and why the tune matters

`[OutputChannels]` holds more than fields. It also declares **expressions**: channels the
firmware computes from other channels and from *tune settings*. `RealtimeDecoder` resolves
them once at construction, against the field names plus whatever tune scalars it was
given, and reports the ones it could not resolve rather than retrying per sample.

That is why the README says to **open the tune before connecting**: injector duty divides
by the cylinder count, and the cylinder count does not come over the wire. Connect without
a tune and those channels are simply absent — 45 of a Speeduino's expressions go
unresolved.

## Roles: finding the channel that does a job

Everything built on "the coolant channel" — the insights, the suggested filters, VE
calibration, the power estimate — goes through `ChannelRoles.Find`, which maps one of 21
`ChannelRole` values onto whatever this firmware happened to call it.

Anything that looks for a single spelling works on one make of controller and silently
does nothing on the others. That is not hypothetical; it has happened repeatedly, and it
never announces itself — the feature just returns empty.

**How a name is matched.**

1. `Simplify` reduces both the channel name and its units: lower case, with spaces,
   underscores, dots, hyphens, tabs, degree signs, colons, slashes and **brackets**
   removed. Firmware groups its channels with a prefix (`SPK: Knock retard`,
   `Fuel: VE`) and qualifies labels with a bracket (`Wheel Speed (kph)`,
   `VE (Current)`); left in, every one of those is invisible to every alias.
2. Each role's aliases are tried **in order, most specific first**. Within one alias, a
   whole-name match is taken before a near one.
3. A near match is `Extends`: the alias plus at most a bank or sensor number — `afr1`,
   `lambdaa`, but never `afrload`.
4. `Suits` checks the units are right for the job, with an empty unit allowed everywhere
   because plenty of firmware declares none.

**Alias order beats match quality, and that ordering is deliberate.** Running every
alias's exact match before any alias's near match lets a late alias beat an early one: an
MS3 logs both `gps_speed` and `vss1`, and `gpsspeed` matching the last alias exactly used
to win over `vss` — the first and most preferred — matching `vss1` with a sensor number
after it. A car with no GPS receiver logs that channel as a flat zero, so every
speed-based filter and insight worked from nothing while reading as though it had found
the speed.

**The units guard is not decoration**, and it cuts both ways. It keeps a MaxxECU's "TPS
input voltage" from being taken for the throttle. It also threw away a Speeduino's entire
mixture: that firmware declares `afr` and `afrTarget` with the unit string `O2`, so the
names matched their aliases exactly and the guard discarded both.

Two traps worth knowing when editing the guard:

- **Write the units as `Simplify` leaves them.** `"g/s"`, `"km/h"`, `"m/s"` and `":1"` all
  contain characters it strips, so as literals they could never match anything. Half of
  the mass-airflow list was inert for exactly that reason.
- **A role that is genuinely absent should stay absent.** rusEFI's warmup figure is a
  multiplier around 1.0 where `WarmupCorrection` is defined as a percentage around 100;
  mapping it would report a cold engine as 99 % lean of where it is. It is left unmatched
  on purpose, as is gauge boost, which that controller does not report separately.

### Coverage, as measured

Against logs this application wrote over the wire from the boards on the bench:

| Board | Before | After |
|---|---|---|
| Speeduino 202501, COM14 | 14 / 21 | **19 / 21** |
| rusEFI 2024.11.17 uaefi, COM8 | 14 / 21 | **19 / 21** |

What each was missing beforehand was not obscure. The Speeduino had no mixture, no
mixture target, no battery voltage, no warmup correction and no road speed. The rusEFI had
no mixture target, no spark advance, no volumetric efficiency, no injector pulse width and
no injector duty. Nothing failed and nothing warned; `LogInsights` simply had nulls where
those channels should have been, and 2,328 tests passed throughout.

The remaining gaps on both are real absences: a Speeduino is speed-density and publishes
no mass airflow, and neither board logs an injector duty this application can reach.

## The settings interface

`[Menu]` and `[UserDefined]` describe the settings pages, read by `TuneInterfaceReader`
into a `TuneInterface` of menus and dialogs.

A dialog is a list of `DialogItem`s:

| Kind | From | Shown as |
|---|---|---|
| `Field`, `Slider` | `field`, `slider` | An editable box, choice or number |
| `ReadOnlyField` | `displayOnlyField` | A value you cannot change |
| `Label`, `Text` | `field` with no constant, `text` | A caption |
| `Panel` | `panel` | Another dialog, embedded |
| `Command` | `commandButton` | Nothing — recorded, not rendered |
| `Gauge`, `Unsupported` | `gauge`, `indicator`, `indicatorPanel`, `liveGraph`, `graphLine` | Nothing |

**A menu entry names one of three things and the file does not say which**: a dialog, a
curve, or a table. `BuildSettingsMenu` tries each in turn, and offers the entry only where
one of them resolves — a menu entry that opens nothing is worse than no menu entry at all.
That rule was first needed for curves, which had been skipped entirely and left 23 of a
MicroSquirt's 131 entries and 48 of an MS3's 246 opening a blank pane: warmup enrichment,
cranking pulsewidth, injector dead time, most of what a tuner actually changes.

**The same rule applies to dialogs, which is newer.** A firmware describes more than its
settings here. rusEFI gives every runtime structure a dialog of its own — `engine_state`,
`trigger_state0`, `fan_control0`, `wideband_state0` — holding an indicator panel and a
live graph and never a field. Offered as settings pages they opened blank: **38 of that
firmware's 147.** `TuneInterface.HasSettings` now decides, walking embedded panels and
guarding against a dialog that embeds itself.

**It takes a predicate, and that is the subtle part.** Filtering on editable fields alone
also drops `veTableDialog`, `cltFuelCorrCurveDialog`, a Speeduino's `warmup` and
`primePW` — dialogs that exist only to hold a **curve or a table**, neither of which is a
dialog. That would have undone the curve fix above. Core cannot know which names are
curves or tables, so the view model passes in a `showable` predicate that can answer.

Measured live over MCP:

| Board | Pages before | Pages after | Blank after |
|---|---|---|---|
| rusEFI 2024.11.17 uaefi | 147 | **105** | 0 |
| Speeduino 202501 | 60 | **60** | 0 |

The Speeduino losing nothing is the check that matters: it is the firmware where the curve
fix lives.

## Re-measuring any of this

`FirmwareChannelRoleTests` pins every role against real firmware channel lists, in
`tests/OpenLogViewer.Tests/Fixtures`:

| Suffix | What it holds |
|---|---|
| `.channels` | The output-channel **field** names and declared units |
| `.logged` | The **datalog labels**, as `TunerStudioSource` resolves them |
| `-bench.logged` | The channel list of a log written **over the wire from the board itself** |

Each fixture is `name<TAB>units`, one channel per line, `#` for comments. The expected
channel is spelled out for all 21 roles including the absences, so adding a role or
loosening an alias has to be answered for on all twelve fixtures. The `-bench` pair owe
nothing to reading an INI correctly, which is what makes them worth keeping.

`TuneInterfaceTests` covers `HasSettings`: a dialog of live readings, a dialog with a
field, a setting found through an embedded panel, a read-only display, a panel naming a
curve, and a dialog that embeds itself.

To regenerate a fixture, take the channel list from the same readers the application uses
— `MsqIni.ReadOutputChannels` into a `RealtimeDecoder` for `.channels`, and the datalog
entries resolved the way `TunerStudioSource` resolves them for `.logged`. For a `-bench`
fixture, connect to the board and export: `--connect COM8 --settle 15000 --export <dir>`
writes `log-channels.csv`, whose header and units rows are exactly the two columns wanted.

**Measure against a `.logged` fixture, never a `.channels` one alone.** Field names give a
number that looks right and is not: rusEFI scored 19/21 on its field names and 12/21 on
its labels, and it is the labels that reach a live session.

## Related

- [Documentation index](README.md)
- [Live connection ▸ Definition files](live-connection.md#definition-files) — where to put one, and where they are searched for
- [Editing a tune](tune-editing.md) — the settings pages a definition builds
- [Architecture](architecture.md)
