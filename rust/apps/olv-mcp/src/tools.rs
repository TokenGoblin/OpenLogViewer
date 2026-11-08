//! The tools the model is offered, ported from `Program.Tools()` in the C# MCP server.
//!
//! **Generated, not typed**: the table below was translated mechanically from the C# source, so
//! that no description is retyped by hand. The names, descriptions, schemas and their order are
//! the C#'s, and `tests/oracle_mcp.rs` compares the whole `tools/list` answer with the one the C#
//! server gives.
//!
//! Described in terms of an engine rather than of HTTP, because the useful thing to tell a model
//! is what a channel means and when a number is trustworthy, not that this is a GET.

use olv_agent::agent::json::J;

fn field(kind: &str, description: &str) -> J {
    J::obj([("type", J::s(kind)), ("description", J::s(description))])
}

fn tool(name: &str, description: &str, properties: Vec<(&str, J)>, required: &[&str]) -> J {
    J::obj([
        ("name", J::s(name)),
        ("description", J::s(description)),
        (
            "inputSchema",
            J::obj([
                ("type", J::s("object")),
                (
                    "properties",
                    J::Obj(properties.into_iter().map(|(k, v)| (k.to_string(), v)).collect()),
                ),
                ("required", J::strs(required)),
            ]),
        ),
    ])
}

/// The settings/cells pair every propose-shaped tool takes.
fn proposal_properties() -> Vec<(&'static str, J)> {
    vec![("settings", settings_field()), ("cells", cells_field())]
}

fn settings_field() -> J {
    J::obj([
        ("type", J::s("array")),
        ("description", J::s("Settings to change, each by name and new value.")),
        (
            "items",
            J::obj([
                ("type", J::s("object")),
                (
                    "properties",
                    J::obj([
                        ("name", field("string", "The setting's name, as olv_tune gives it.")),
                        (
                            "value",
                            field("number", "The new value, in the units the firmware declares."),
                        ),
                    ]),
                ),
            ]),
        ),
    ])
}

fn cells_field() -> J {
    J::obj([
        ("type", J::s("array")),
        (
            "description",
            J::s("Table cells to change, each by table name, column, row and new value."),
        ),
        (
            "items",
            J::obj([
                ("type", J::s("object")),
                (
                    "properties",
                    J::obj([
                        ("table", field("string", "The table name.")),
                        ("column", field("integer", "Column index, from zero.")),
                        ("row", field("integer", "Row index, from zero.")),
                        ("value", field("number", "The new value.")),
                    ]),
                ),
            ]),
        ),
    ])
}

/// What the model is offered.
pub fn tools() -> J {
    J::Arr(vec![
        tool(
            "olv_state",
            concat!(
                "What OpenLogViewer has open: whether it is watching a live ECU or a saved log, ",
                "the firmware, how many samples and channels, and whether writing is armed. ",
                "Ask this first; everything else depends on what is loaded."
            ),
            vec![],
            &[],
        ),
        tool(
            "olv_channels",
            concat!(
                "Every channel available, with its units and the job it does. The role — EngineSpeed, ",
                "Mixture, ManifoldPressure and so on — is the reliable way to find a channel, ",
                "because each firmware spells them differently: a rusEFI calls engine speed ",
                "RPMValue and a MegaSquirt calls it rpm."
            ),
            vec![(
                "raw",
                field(
                    "boolean",
                    concat!(
                        "True to get every channel the connected ECU's firmware decodes, not just the ",
                        "ones its own datalog definition names — for finding something nobody thought ",
                        "to log. Only live connections have anything extra to offer here."
                    ),
                ),
            )],
            &[],
        ),
        tool(
            "olv_wire_health",
            concat!(
                "How the link to the ECU is actually doing: recent success rate, what kinds of failures ",
                "happened, how long since the last one, and how long ago the last successful exchange ",
                "was. Ask this before concluding a read or write failed for a reason in the tune rather ",
                "than the link."
            ),
            vec![],
            &[],
        ),
        tool(
            "olv_wire_events",
            concat!(
                "The most recent requests actually sent to the ECU, newest first: what each one was ",
                "for, which attempt it was, how long it took, and how it ended. This is the wire ",
                "trace — the thing to pull when a read or write behaved unexpectedly and olv_state ",
                "or a refusal message alone does not say why."
            ),
            vec![(
                "count",
                field(
                    "integer",
                    "How many events to return, newest first. Defaults to 100, capped at 500.",
                ),
            )],
            &[],
        ),
        tool(
            "olv_values",
            concat!(
                "The samples of one channel, oldest first, with the matching times. On a live ",
                "session give a number of seconds to get only the newest; leave it out for the lot."
            ),
            vec![
                (
                    "channel",
                    field("string", "The channel name, exactly as olv_channels gives it."),
                ),
                (
                    "seconds",
                    field(
                        "number",
                        "How many seconds back from the newest sample. Omit for all of them.",
                    ),
                ),
            ],
            &["channel"],
        ),
        tool(
            "olv_insights",
            concat!(
                "What the log says about the engine: fuelling against target, lean excursions under ",
                "load, closed-loop behaviour, sensor faults, and what looks healthy. Each finding ",
                "carries the arithmetic behind it so it can be argued with."
            ),
            vec![],
            &[],
        ),
        tool(
            "olv_tune",
            concat!(
                "Every setting in the tune read off the controller, by name. Empty unless an ECU is ",
                "connected and its tune has been read."
            ),
            vec![],
            &[],
        ),
        tool(
            "olv_tables",
            concat!(
                "The names of the tables this firmware declares — the VE table, the ignition table ",
                "and the rest."
            ),
            vec![],
            &[],
        ),
        tool(
            "olv_table",
            "One table: its cells as rows, and the axes they are looked up on.",
            vec![("name", field("string", "The table name, as olv_tables gives it."))],
            &["name"],
        ),
        tool(
            "olv_project",
            concat!(
                "The tuning project for this vehicle: what is wrong with the tune, what has already ",
                "been tried, and what happened. READ THIS FIRST on any tuning question — it is the ",
                "part no log can tell you, and without it you will re-diagnose problems that were ",
                "diagnosed weeks ago and re-suggest changes that were already tried and did not work."
            ),
            vec![],
            &[],
        ),
        tool(
            "olv_record_sitting",
            concat!(
                "Records the log in hand against the project: keeps every finding, raises a fix for ",
                "anything newly warned about, and notes a repeat against the fix already tracking ",
                "it. Do this once per log you analyse, so the project shows whether things are ",
                "getting better."
            ),
            vec![(
                "note",
                field(
                    "string",
                    "What this sitting was, in your words. What was changed before it, what you were testing.",
                ),
            )],
            &[],
        ),
        tool(
            "olv_keep_tune",
            concat!(
                "Records the tune now in hand as a version of the project, so later logs can be ",
                "attributed to it. Do this before suggesting a change and after one is burned. ",
                "Recording an unchanged tune is harmless — it returns the version it already is."
            ),
            vec![(
                "note",
                field("string", "Why this tune is worth keeping. What it was for."),
            )],
            &[],
        ),
        tool(
            "olv_compare_versions",
            concat!(
                "What changed between two tune versions, setting by setting, in the firmware's own ",
                "units. Version ids look like v1, v2 and come from olv_project. This is how to ",
                "check what a change actually did rather than what it was meant to do."
            ),
            vec![
                ("from", field("string", "The earlier version, e.g. v3.")),
                ("to", field("string", "The later one, e.g. v4.")),
            ],
            &["from", "to"],
        ),
        tool(
            "olv_note_fix",
            concat!(
                "Adds a fix to the project or moves one along. Give an id to change an existing one, ",
                "or leave it out to raise a new one. This changes the record of what is being ",
                "worked on and touches no engine, so it needs no arming. Move a fix to applied when ",
                "a change has been made and to verified only when a later log shows it worked."
            ),
            vec![
                (
                    "id",
                    field(
                        "string",
                        "The fix to change, as olv_project gives it. Omit to raise a new one.",
                    ),
                ),
                (
                    "title",
                    field("string", "What is wrong, in one line. Required for a new fix."),
                ),
                (
                    "detail",
                    field("string", "The reasoning: what was seen and what you think it means."),
                ),
                ("state", field("string", "open, applied, verified or abandoned.")),
                ("change", field("string", "What was actually changed in the tune.")),
            ],
            &[],
        ),
        tool(
            "olv_set_setting",
            concat!(
                "Changes one setting in the ECU's WORKING MEMORY. Refused unless the person at the ",
                "machine has ticked \"Allow agent writes\", unless the engine is running above idle, ",
                "unless too many writes have landed in the last few seconds, and unless the change is ",
                "disproportionately large for one call. It is never burned, so turning the key off ",
                "undoes it. Read the value first and say what you are changing and why: rationale is ",
                "required and is echoed back with the write. A rev limiter, launch RPM, boost limit, ",
                "or fuel/ignition cut additionally needs confirmDangerous:true."
            ),
            vec![
                ("name", field("string", "The setting's name, as olv_tune gives it.")),
                (
                    "value",
                    field("number", "The new value, in the units the firmware declares."),
                ),
                (
                    "rationale",
                    field("string", "Why this change is being made, in one line. Required."),
                ),
                (
                    "confirmDangerous",
                    field(
                        "boolean",
                        concat!(
                            "Set true to confirm a write to a rev limiter, launch RPM, boost limit, or ",
                            "fuel/ignition cut. Refused without it only for those settings."
                        ),
                    ),
                ),
            ],
            &["name", "value", "rationale"],
        ),
        tool(
            "olv_set_table_cell",
            concat!(
                "Changes one cell of one table in the ECU's WORKING MEMORY. Refused unless writing ",
                "is armed, unless the engine is running above idle, unless too many writes have ",
                "landed in the last few seconds, and unless the change is disproportionately large ",
                "for one call. Never burned. Columns and rows are counted from zero. Rationale is ",
                "required and is echoed back with the write."
            ),
            vec![
                ("table", field("string", "The table name.")),
                ("column", field("integer", "Column index, from zero.")),
                ("row", field("integer", "Row index, from zero.")),
                ("value", field("number", "The new value.")),
                (
                    "rationale",
                    field("string", "Why this change is being made, in one line. Required."),
                ),
                (
                    "confirmDangerous",
                    field(
                        "boolean",
                        "Set true to confirm a write to a constant one of the safety guards recognises.",
                    ),
                ),
            ],
            &["table", "column", "row", "value", "rationale"],
        ),
        tool(
            "olv_tune_full",
            concat!(
                "The whole tune in one call: every setting with its units/options/range, and every ",
                "table with its cells and axes. Use this instead of olv_tune plus olv_tables plus ",
                "an olv_table call per table when you want to reason about the tune as a whole."
            ),
            vec![],
            &[],
        ),
        tool(
            "olv_log_full",
            concat!(
                "Every channel's samples from the log in hand, in one call, sharing one time column. ",
                "A full, undecimated log can be tens of megabytes of JSON, so give a decimate stride ",
                "(e.g. 5 keeps one sample in five) on anything but a short log."
            ),
            vec![
                (
                    "seconds",
                    field(
                        "number",
                        "How many seconds back from the newest sample. Omit for all of them.",
                    ),
                ),
                (
                    "decimate",
                    field(
                        "integer",
                        "Keep one sample in every this many. Omit or 1 for all of them.",
                    ),
                ),
            ],
            &[],
        ),
        tool(
            "olv_context",
            concat!(
                "The one call to make at the start of a session: the project's own prose, what is ",
                "connected, how big the tune in hand is, the latest findings and the link's health. ",
                "Deliberately not the full tune or the full log — ask olv_tune_full or olv_log_full ",
                "next if this says they are worth reading."
            ),
            vec![],
            &[],
        ),
        tool(
            "olv_propose_tune",
            concat!(
                "Works out what a batch of settings and table cells would change, WITHOUT sending ",
                "anything. Answerable even when writes are not armed, because nothing here reaches ",
                "the ECU — it lays the changes over a private copy of the tune and reports the ",
                "difference. Use this before olv_apply_tune so the person can see the diff first."
            ),
            proposal_properties(),
            &[],
        ),
        tool(
            "olv_apply_tune",
            concat!(
                "Applies a batch of settings and table cells to the ECU's WORKING MEMORY for real, ",
                "through the same per-setting and per-cell writes olv_set_setting and ",
                "olv_set_table_cell use — the same guards apply: writing must be armed, the engine ",
                "must not be running above idle, and too large or too frequent a batch is refused. ",
                "Never burned. \"note\" doubles as the rationale each underlying write requires and ",
                "is what the version gets kept under, so it is required, not optional."
            ),
            vec![
                ("settings", settings_field()),
                ("cells", cells_field()),
                (
                    "note",
                    field(
                        "string",
                        "Why this batch is being applied, in one line. Required — kept as the version note.",
                    ),
                ),
                (
                    "confirmDangerous",
                    field(
                        "boolean",
                        concat!(
                            "Set true if the batch touches a rev limiter, launch RPM, boost limit, or ",
                            "fuel/ignition cut. Refused without it only when one of those is touched."
                        ),
                    ),
                ),
            ],
            &["note"],
        ),
        tool(
            "olv_stage_tune",
            concat!(
                "Writes a tune out as a real .msq file in OpenLogViewer's staging folder, for a ",
                "person to open in TunerStudio themselves — nothing here reaches the ECU, so it ",
                "needs no arming. With no settings or cells, stages the tune exactly as it stands; ",
                "given some, stages what they would make it, the same working copy olv_propose_tune ",
                "uses."
            ),
            vec![
                ("settings", settings_field()),
                ("cells", cells_field()),
                (
                    "filename",
                    field(
                        "string",
                        "The file name to stage it under. Defaults to a timestamped name.",
                    ),
                ),
            ],
            &[],
        ),
        tool(
            "olv_stage_table",
            concat!(
                "Writes one named table out as an import-ready CSV in the staging folder — ",
                "breakpoints across the top and down the left, values in the grid. Never reaches ",
                "the ECU, so it needs no arming."
            ),
            vec![
                ("name", field("string", "The table name, as olv_tables gives it.")),
                (
                    "filename",
                    field(
                        "string",
                        "The file name to stage it under. Defaults to a timestamped name.",
                    ),
                ),
            ],
            &["name"],
        ),
        tool(
            "olv_list_staged",
            concat!(
                "What is sitting in the staging folder right now: every file olv_stage_tune or ",
                "olv_stage_table has written, with its size and when it was written."
            ),
            vec![],
            &[],
        ),
        tool(
            "olv_definition_needed",
            concat!(
                "Whether OpenLogViewer is missing the firmware definition (.ini) for the ECU somebody ",
                "just tried to connect to, and what it needs. ASK THIS whenever a connection fails — ",
                "a definition is how raw ECU bytes become named channels and settings, and without ",
                "the right one the application will not connect at all. The answer says what the ECU ",
                "called itself, what is already on this machine, and — only where the firmware's ",
                "licence permits it — addresses to fetch the file from. Read the guidance field: when ",
                "it gives you no addresses that is a decision about that firmware, not an oversight, ",
                "and you should follow what it says instead rather than searching for a way around it."
            ),
            vec![],
            &[],
        ),
        tool(
            "olv_import_definition",
            concat!(
                "Hands a firmware definition to OpenLogViewer, which checks it really is one and really ",
                "declares the signature the ECU reported before keeping it. Prefer \"path\": save the ",
                "file yourself and give the path, because these run to half a megabyte and pasting one ",
                "through \"content\" wastes an enormous amount of context for no gain. A definition ",
                "decides which byte of an engine is the rev limiter, so a mismatch is refused rather ",
                "than guessed at."
            ),
            vec![
                ("path", field("string", "Where you saved the file. The ordinary way.")),
                (
                    "content",
                    field("string", "The definition itself, only if you have nowhere to save it."),
                ),
                (
                    "source",
                    field(
                        "string",
                        "Where it came from — the URL you fetched, so it is on the record.",
                    ),
                ),
                (
                    "name",
                    field("string", "What to call it. Omit to name it after its signature."),
                ),
            ],
            &[],
        ),
    ])
}
