# Agent console: commands button and Tab completion (plan)

Status: planned, not started. Builds on the console added in `3dc9e38`
(branch `agent-console`).

## Goal

1. A **Commands** button in the console's top bar that lists every command,
   and puts a command into the prompt when one is clicked.
2. **Tab completion** at the `olv>` prompt for command names, keywords, and
   the names that change per ECU (tables, channels, settings).

## Step 1: one list of commands (refactor, no visible change)

Today the commands are spelled out three times: the `switch` in
`AgentConsoleCommand.Parse`, the `Help` string, and soon the button and
completion. Replace the `Help` string with a structured catalog in Core:

```csharp
public sealed record ConsoleVerb(
    string Name,              // "cell"
    string Usage,             // "cell <table> <column> <row> <value> [why…] [--dangerous]"
    string Summary,           // "change one table cell in working memory"
    string Group,             // Reading | Writing | Anything else | The console
    bool IsWrite,
    IReadOnlyList<ArgumentKind> Arguments);   // Table, Int, Int, Number, Text…

public enum ArgumentKind { Table, Channel, Setting, Route, Version, Keyword, Number, Int, Text }
```

- Build `Help` from the catalog, so the text and the parser can't drift apart.
- Keywords live on the catalog entry: `channels raw`, `tune full`,
  `api on|off`, `show all|ai|console|writes`.
- **Test:** every verb `Parse` accepts is in the catalog, and every catalog
  verb parses. This is what keeps the button and completion honest.

## Step 2: the Commands button

- Add a **Commands** button in the top bar, beside **Clear**.
- Clicking it opens a popup anchored under the button, grouped by `Group`. Each
  row shows the usage in monospace and the summary in muted text. Write
  commands are in the orange used for writes everywhere else.
- Clicking a row puts the verb and a trailing space into the prompt (e.g.
  `table `), closes the popup, and focuses the prompt with the cursor at the
  end. It **never sends**. A write command is only ever sent by pressing Enter.
- The popup closes on Esc or a click outside. A filter box at the top of the
  popup is optional: there are about 25 commands, so it's not needed yet.
- Leave `help` as it is. It's the typed equivalent and still works without a
  mouse.

## Step 3: Tab completion, core logic

A pure function in Core, so it can be tested without WPF:

```csharp
public sealed record Completion(int ReplaceStart, int ReplaceLength, IReadOnlyList<string> Candidates);

public static Completion Complete(string line, int caret, IConsoleNames names);

public interface IConsoleNames
{
    IReadOnlyList<string> Tables { get; }
    IReadOnlyList<string> Channels { get; }
    IReadOnlyList<string> Settings { get; }
    IReadOnlyList<string> Versions { get; }
}
```

- Work out which word the cursor is in, including a word that is still inside
  open quotes (`table "VE Ta|`). This needs a tokenizer that reports word spans
  and whether a quote is still open. The current `Split` only returns strings.
- Word 0 completes to verb names. Later words complete according to the
  verb's `Arguments[i]`:
  - Table: `table`, `cell` (word 1)
  - Channel: `values` (word 1)
  - Setting: `set` (word 1)
  - Route: `get`, `post`, completed from the route list the server already
    publishes at `GET /`
  - Version: `versions` (words 1 and 2)
  - Keyword: from the catalog entry
  - Number, Int, Text: no completion. Show the usage hint instead (Step 5).
- **Matching:** a case-insensitive prefix match first. If no prefix matches,
  fall back to matching anywhere in the name, so `ign` finds
  `Ignition Advance Table`.
- **Quoting:** a candidate with a space in it is inserted quoted
  (`"VE Table"`). The replaced span includes any quote already typed.
- `--dangerous` is offered only after `set` and `cell`, and only once that
  command's required arguments are there.

**Tests:** cursor in each position, a part-typed quoted name, no matches, a
single match, several matches, and the fallback match.

## Step 4: Tab completion in the window

Where the names come from:
- **Read them in-process from the view model, the same data `AgentBridge`
  serves, not over HTTP.** Fetching them through the API would put a console
  request in the transcript on every Tab press, burying what the AI is doing.
- This doesn't break the console's rule that it has no private way in: that
  rule is about what can be changed, and completion only reads names.
- Refresh the names when the connection or tune changes. A cheap snapshot on
  each Tab press is fine at these sizes, a few hundred names at most.

Keys, all handled in `PreviewKeyDown` so Tab doesn't move focus:

| Key | Suggestion list closed | Suggestion list open |
|---|---|---|
| Tab | One match: insert it plus a space. Several: fill in their common prefix, then open the list | Accept the highlighted one |
| Shift+Tab | — | Move up |
| Up / Down | Command history, as now | Move through the suggestions |
| Enter | Run the line | Accept, don't run |
| Esc | Clear the line | Close the list |

- The suggestion list sits under the prompt and shows at most about 10 rows,
  scrolling past that. Each row shows the name, plus units for a channel or
  the range for a setting when known. Without that, a list of 81 channel
  names isn't much use.
- Typing narrows an open list live. It closes when nothing matches.

## Step 5 (optional): usage hint

Show the current verb's remaining usage faintly after the cursor, the way
shells do. For example, after typing `cell veTable1 4 ` it shows
`<row> <value> [why…]`. It's display-only, and it's what covers the number and
text arguments that Tab can't complete.

## Order and size

| Step | What | Rough size |
|---|---|---|
| 1 | Command catalog and consistency test | small |
| 2 | Commands button and popup | small |
| 3 | Completion core and tests | medium, mostly the tokenizer |
| 4 | Window keys, suggestion list, names from the view model | medium |
| 5 | Usage hint | small, can be skipped |

Steps 1–2 can ship alone. Steps 3–4 should ship together.

## Open questions

- **Completing table cells:** `cell veTable1 <col> <row>` could offer axis
  values, such as completing a column from an RPM breakpoint. That's useful,
  but it's a different feature. Left out.
- **Completing values:** `set reqFuel <Tab>` could offer the current value as
  a starting point. It's cheap once settings are loaded. Worth asking for.
- **Saved history:** keeping command history between sessions in the
  workspace. Small, but separate.
