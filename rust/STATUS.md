# Port status: handoff for the next session

Written 2026-11-08 at the end of a session. Read this, then `PLAN.md`, `CONVENTIONS.md` and
`UI-SPEC-OPEN-QUESTIONS.md` (the user's answers are at the top).

## Where it stands

- **Merged into `rust-port`:** Waves 0–5 except `w5-agent`, plus the fix-all-defects pass,
  the interface spec, and the C# Retro/zoom code (the interface reference).
  - 2,257 tests pass. clippy, fmt, `cargo deny` and `xtask check` are clean.
  - The committed C# references regenerate with no drift.
- **By C# lines:** about 62% of all lines, and about 94% of non-interface code (Wave 5 agent
  pending). Interface: spec done, base built (not merged), screens not started.

## Branches pushed but **not merged**, and why

| Branch | State | Before it can merge |
|---|---|---|
| `w5-agent` | Agent API, MCP server, the four console tools. Two review rounds; all second-round findings fixed (515dd19), 2,477 tests. | A **third Opus review** of the fix commit. It's the AI ↔ engine boundary, so don't merge on the porter's word. |
| `w6-ui-base` | 15 themes (byte-exact against the C#), OLV Retro, zoom, Windows title bar, the `olv-themes` demo. | Remove its `deny.toml` exception for egui's bundled fonts (OFL/Ubuntu). The user decided to **draw our own UI font** instead (next step). |

## Next steps, in order

1. **Our own UI font (user decision).** An original, MIT, full-coverage UI font and a data
   (monospace) font, drawn like OLV Retro and built by `olv-fontgen`. Then turn off egui's
   `default_fonts` feature, drop the `deny.toml` exception from `w6-ui-base`, and merge it.
2. Third review of `w5-agent`, then merge it.
3. Wave 7: the five screen packages. `cargo xtask scaffold 7`, briefs from `xtask brief`.
   Every gate and confirmation is in `UI-SPEC.md` §0 and §8.
4. Waves 8 and 9: view model, then the shell. `notes/w8-viewmodel.md` lists what the view
   model must wire for the agent gates, lost-write re-sends and the confirmations.
5. Hardware checks on Windows, Linux and macOS (`PLAN.md`, Hardware table).

## The C# app (shipping)

Fix branches from `main`, each reviewed, **not merged** (the user hasn't decided):
- `fix-burn-after-reopen`: a burn after a reconnect committed the old tune and reported success.
- `fix-maxx-write-ack`: a stale byte made a refused MaxxECU write report success.

Agent-safety defects in the C# that are **not** fixed there yet: batch magnitude, definition
import and unknown rpm (`CSHARP-DEFECTS.md`). They matter only with AI writes armed.

## Traps that cost time

- **Corpus references** are gitignored and per working copy. A corpus test with no reference
  returns early and passes. After any merge, run `cargo xtask corpus`.
- **Never run Python that writes Rust or C#** containing `\n`, `\t` or similar. It turned them
  into real control characters several times. Use the Edit tool for those.
- Merging branches made at different times can break the build through API changes
  (`use_layout` became `Result`). Always run clippy after a merge, before committing.
