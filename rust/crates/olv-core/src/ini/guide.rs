//! Ported from `Guide.cs`. Owned by package `w1-ini`.
//!
//! The manual, carried inside the application.
//!
//! In here rather than behind a link because of where this gets used. The people
//! this is for plug a laptop into a car, often in a garage with no internet and
//! no phone signal. Written as data rather than as a page of markup so that it
//! can be searched, re-themed, and checked by a test.

/// One thing you can do, and what is worth knowing about doing it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct GuideEntry {
    /// What it is, as a person would ask for it.
    pub title: &'static str,
    /// How to do it, and why it behaves as it does where that is not obvious.
    pub body: &'static str,
    /// Keyboard or mouse shortcut, where there is one.
    pub keys: &'static str,
}

/// `string.Contains(text, StringComparison.OrdinalIgnoreCase)`.
fn contains_ignore_case(haystack: &str, needle: &str) -> bool {
    haystack.to_uppercase().contains(&needle.to_uppercase())
}

impl GuideEntry {
    pub fn has_keys(&self) -> bool {
        !self.keys.is_empty()
    }

    /// Whether a search for `text` should turn this up.
    pub fn matches(&self, text: &str) -> bool {
        contains_ignore_case(self.title, text)
            || contains_ignore_case(self.body, text)
            || contains_ignore_case(self.keys, text)
    }
}

/// A group of related entries.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct GuideSection {
    pub title: &'static str,
    pub blurb: &'static str,
    pub entries: &'static [GuideEntry],
}

/// The sections of the guide, in order.
pub fn sections() -> &'static [GuideSection] {
    SECTIONS
}

/// Every entry, for searching across sections.
pub fn all_entries() -> impl Iterator<Item = &'static GuideEntry> {
    SECTIONS.iter().flat_map(|s| s.entries.iter())
}

static SECTIONS: &[GuideSection] = &[
    GuideSection {
        title: "Getting started",
        blurb: "Open a recording and get something on screen.",
        entries: &[
            GuideEntry {
                title: "Open a log",
                body: "Open log… in the toolbar, or drop a file onto the window. Nothing is assumed from the extension — the content is examined instead, so a log renamed by a browser or an email client still opens.",
                keys: "Ctrl+O",
            },
            GuideEntry {
                title: "What it reads",
                body: "MegaSquirt and TunerStudio .mlg and .msl, rusEFI, MaxxECU's zipped logs, and delimited text from MoTeC, Haltech, Link, AEM, ECUMaster, Holley, HP Tuners, Speeduino and most anything else that exports CSV. The text reader works out the delimiter, the encoding, the units row and the time base for itself, including European decimal commas.",
                keys: "",
            },
            GuideEntry {
                title: "If a log will not open",
                body: "The message says what was wrong with the file rather than that something failed. A format that is close but not handled is usually easy to add — the reader is one file.",
                keys: "",
            },
            GuideEntry {
                title: "Three ways to read a recording",
                body: "Log draws the channels against time. Histogram bins them into a table shaped like the one in your ECU. Scatter puts every sample at its own X and Y. They are the same samples and the same settings; only the question differs.",
                keys: "",
            },
        ],
    },
    GuideSection {
        title: "Reading the plot",
        blurb: "The channel list, the traces, and getting about the recording.",
        entries: &[
            GuideEntry {
                title: "Choose channels",
                body: "Tick them in the list on the left. Common plots the set most logs are read for in one click; All and None do what they say.",
                keys: "",
            },
            GuideEntry {
                title: "Find a channel",
                body: "The box at the top of the list matches on name, units or category. Sort by category, A–Z, or plotted-first.",
                keys: "",
            },
            GuideEntry {
                title: "Hide unused",
                body: "On by default. Logs routinely declare channels that never move — 98 of 179 in one sample — and hiding them is what makes the rest findable. They are still recorded and still exported.",
                keys: "",
            },
            GuideEntry {
                title: "Read a value",
                body: "Move the pointer over the plot and every row shows its value there. The trace nearest the pointer thickens, its row highlights, and a card gives its value plus its highest and lowest — and the moment each happened.",
                keys: "",
            },
            GuideEntry {
                title: "Jump to an extreme",
                body: "The ▲ and ▼ buttons on a channel row, or right-click it. Clicking the max or min line in the hover card does the same. The zoom is kept, and the channel is plotted first if it was not showing.",
                keys: "",
            },
            GuideEntry {
                title: "Zoom and pan",
                body: "Scroll to zoom at the pointer, drag to pan, double-click to fit the whole log. Reset zoom in the View menu goes back to everything.",
                keys: "",
            },
            GuideEntry {
                title: "Mark a span",
                body: "Shift-drag across the plot. Every channel row switches to min … max and the average over that span, and the hover card describes the span rather than the whole log. Click the plot to clear it.",
                keys: "Shift-drag",
            },
            GuideEntry {
                title: "Overlaid or stacked",
                body: "Overlaid traces each scale to their own range and read well for timing between channels. Stacked gives each its own strip, which is what you want past about four.",
                keys: "",
            },
            GuideEntry {
                title: "Gaps are drawn as gaps",
                body: "A paused-and-resumed log leaves a hole, and a straight line across it would read as steady data that was never recorded. The pen lifts when the step between samples exceeds ten times the log's usual interval.",
                keys: "",
            },
            GuideEntry {
                title: "Steady channels look steady",
                body: "A sensor holding almost still would otherwise have its last decimal place stretched to fill the lane, and a pressure holding 12.0 within a tenth would be drawn as a wall of noise. Turn it off in the View menu when a small drift is exactly what you are chasing.",
                keys: "",
            },
            GuideEntry {
                title: "Save a selection",
                body: "Plot what you want and press + Save. The preset becomes a chip you can click to restore it. Presets are held by channel name, so one saved on a log applies to any other that shares those names. Right-click a chip to overwrite or delete.",
                keys: "",
            },
        ],
    },
    GuideSection {
        title: "Finding a moment",
        blurb: "Jump to where something happened.",
        entries: &[
            GuideEntry {
                title: "Search the log",
                body: "Type a condition — RPM > 4500 && TPS > 80 — and every stretch of the drive that met it is shaded. Enter steps forward through them, shift-Enter back, Escape closes the bar.",
                keys: "Ctrl+F",
            },
            GuideEntry {
                title: "It uses the calculated-channel syntax",
                body: "On purpose: anyone who has written one already knows how to write a search, and a condition that proves useful can be pasted into a calculated channel or a filter without translation.",
                keys: "",
            },
            GuideEntry {
                title: "Runs, not crossings",
                body: "A signal sitting near its threshold crosses it repeatedly, and reporting each crossing would bury the one thing that happened. A brief dip below is bridged, so consecutive matches are one finding.",
                keys: "",
            },
            GuideEntry {
                title: "What it will not claim",
                body: "A sample where a named channel has no reading is counted as could not be judged, separately from the misses — a comparison against a reading that was never taken is unanswerable rather than false. Filters still apply, since they say which part of the drive is under consideration.",
                keys: "",
            },
        ],
    },
    GuideSection {
        title: "The heat table",
        blurb: "Binning a drive into the shape of a tuning table.",
        entries: &[
            GuideEntry {
                title: "Build one",
                body: "Switch to Histogram. Pick any three channels: two axes and a value. Each cell reduces the samples that landed in it by mean, min, max or count.",
                keys: "",
            },
            GuideEntry {
                title: "Use the tune's own axes",
                body: "Every .mlg carries the tune it was recorded with, so the ECU's own breakpoints can be read straight out of the log. Pick one under Axis breakpoints and the table lines up cell for cell with the one in TunerStudio. Uniform bins never do, because ECU axes are tight at idle and wide up top.",
                keys: "",
            },
            GuideEntry {
                title: "Open a tune by hand",
                body: "Open tune… for a .msl or .csv, which carry none. For an .mlg it is usually the wrong thing to do: VE Calibration scales the numbers that produced the logged mixture, and a tune edited since the drive would have it scale numbers the engine never ran.",
                keys: "",
            },
            GuideEntry {
                title: "Only the zoomed range",
                body: "Restricts the table to the window you zoomed to in the log view, so it can be built from a single pull rather than the whole drive.",
                keys: "",
            },
            GuideEntry {
                title: "Colour by sample count",
                body: "Re-shades by how much data backs each cell — the quick way to see which parts of the map the drive actually exercised.",
                keys: "",
            },
            GuideEntry {
                title: "Trace a cell back to the log",
                body: "Click a populated cell. The view switches to the plot, frames the samples that made it, and marks them. An engine passes through the same cell many times, so the samples are grouped into visits: the longest is framed, the rest are shaded, and the count is reported. A cell averaged over twelve passes is a very different thing from one sustained pull.",
                keys: "Click a cell",
            },
            GuideEntry {
                title: "And the other way round",
                body: "Mark a span on the plot, switch to Histogram, and every cell that stretch passed through is ringed. Those are the cells the pull is evidence about, and the ones worth editing on the strength of it.",
                keys: "",
            },
            GuideEntry {
                title: "Data filters",
                body: "A table built from a whole drive averages warmup, overrun and idle into the same cells as the pulls you care about. Each filter is a condition on a channel and they combine with AND. Suggestions appear for the channels a log has and always arrive switched off, so opening a file never silently changes what is counted.",
                keys: "",
            },
            GuideEntry {
                title: "What filtering does to the axes",
                body: "They re-scale to the samples that survive. Filtering to warm running also tightens the RPM and load range onto that data, which is usually what you want — but it means two differently-filtered tables are not comparable cell for cell.",
                keys: "",
            },
        ],
    },
    GuideSection {
        title: "Scatter",
        blurb: "Every sample where it fell, coloured by a third channel.",
        entries: &[
            GuideEntry {
                title: "What it shows that a table cannot",
                body: "Spread. A cell reading dead on target looks the same whether it was measured twelve times at target or six times rich and six times lean, and that difference is most of what a log has to say about whether a region is settled or merely averages well.",
                keys: "",
            },
            GuideEntry {
                title: "Marks are computed, not raced for",
                body: "A drive is tens of thousands of samples in a small part of the map. Drawn one mark per sample they stack thousands deep and the surviving colour is whichever was drawn last — an accident of log order. Samples are aggregated onto the display's own grid instead, so every mark is the mean of what landed under it.",
                keys: "",
            },
            GuideEntry {
                title: "Hover a mark",
                body: "It reports the count behind it and the range its samples covered. The spread is exactly what a mean hides, so it is said rather than left to be inferred.",
                keys: "",
            },
            GuideEntry {
                title: "The colour scale is trimmed",
                body: "A wideband touches both its rails during a transient, and scaled over the full range those few blocks own the ramp while the drive draws as one flat colour. The ends are trimmed and anything past them saturates — still drawn, still the most extreme mark, still exact on hover. The legend marks a bound ≤ or ≥ only when the trim moved it far enough to matter.",
                keys: "",
            },
            GuideEntry {
                title: "Click a mark",
                body: "Traces back to the log the same way a table cell does.",
                keys: "Click a mark",
            },
        ],
    },
    GuideSection {
        title: "Insights",
        blurb: "What the log says about the engine that produced it.",
        entries: &[
            GuideEntry {
                title: "Ask it",
                body: "The Insights button, beside Calculators. It measures the log and reports what it found: fuelling against target, lean excursions under load, knock retard, closed-loop behaviour, injector duty, charging, warmup, and sensors that never moved. It needs nothing but the log — no ECU and no network.",
                keys: "",
            },
            GuideEntry {
                title: "Read the levels",
                body: "Warning is worth stopping for. Watch is worth a look before the next drive. Note is context. Good means it was checked and is as it should be, which is worth saying: an analysis that only ever complains cannot tell you anything is right. Not measured means the log could not answer, and says why.",
                keys: "",
            },
            GuideEntry {
                title: "Every finding shows its arithmetic",
                body: "The small grey line under each one is what it rests on — how many samples, the median and the spread, where the worst of it was. It is there so a finding can be argued with rather than believed.",
                keys: "",
            },
            GuideEntry {
                title: "Lean under load is the one to read first",
                body: "Averages hide it. A single lean excursion at high load is what damages a piston, and it does not need to be common to do it, so this counts the samples rather than averaging them.",
                keys: "",
            },
            GuideEntry {
                title: "Some questions need a barometer",
                body: "Boost is manifold pressure above atmospheric, and nothing in a log says what atmospheric was unless the controller recorded it. Failing that this reads it off the manifold with the engine stopped, so starting a log before cranking gives it one. With neither it says it cannot tell boost from altitude, rather than guessing.",
                keys: "",
            },
            GuideEntry {
                title: "Away from the window",
                body: "The same analysis runs from a command line as olv-insights, over one log or a folder of them, with no display and no ECU. It exits 0 when nothing is wrong, 1 for something to watch and 2 for a warning, so a script can act on a drive without reading anything.",
                keys: "",
            },
        ],
    },
    GuideSection {
        title: "The tuning project",
        blurb: "What is wrong with this tune, what you tried, and whether it worked.",
        entries: &[
            GuideEntry {
                title: "Why it exists",
                body: "The analysis is the easy half. A log tells you the mixture is lean above 150 kPa; what it cannot tell you is that you knew that three weeks ago, put four per cent into the top of the VE table, and it got better but not right. That half normally lives in somebody's head and in a folder of files called claude01 through claude07.",
                keys: "",
            },
            GuideEntry {
                title: "Open one",
                body: "Tools ▸ Tuning project. Name the vehicle, or pick one you have already. Everything below belongs to that car.",
                keys: "",
            },
            GuideEntry {
                title: "Record a sitting",
                body: "Record this log runs the insights and keeps what they found. Put what you changed beforehand in the note box first — \"after VE +4% above 150 kPa\" is the difference between a row of findings and evidence about a change, and it is the thing nobody writes down unless asked while they still know it.",
                keys: "",
            },
            GuideEntry {
                title: "Fixes",
                body: "A warning raises one the first time it is seen. Seeing the same fault again notes it against the fix already open rather than raising a second — and on a fix you have already applied, that note is the evidence the change did not work. Each carries what is wrong, what you changed, and what has been seen since.",
                keys: "",
            },
            GuideEntry {
                title: "Copy",
                body: "Puts the whole project on the clipboard as plain text. That is what to paste to an assistant: it is the part no single log can tell it.",
                keys: "",
            },
        ],
    },
    GuideSection {
        title: "Tune versions",
        blurb: "Which tune is which, and what changed between them.",
        entries: &[
            GuideEntry {
                title: "What a version is",
                body: "A tune kept at a moment worth keeping, with why it was kept. It sits in the project's folder as an ordinary .msq that TunerStudio still opens — a history nothing else can read would be a trap rather than a feature.",
                keys: "",
            },
            GuideEntry {
                title: "The same tune is not kept twice",
                body: "Identity is the bytes. Reading the tune at the start of two sessions that changed nothing gives one version, and burning a version already recorded is news about it rather than a new one.",
                keys: "",
            },
            GuideEntry {
                title: "What changed",
                body: "Two versions are compared setting by setting, not byte by byte — two tunes can differ in bits no constant declares and be the same tune. What you get back is a list of names and values, in the firmware's own units.",
                keys: "",
            },
            GuideEntry {
                title: "Which tune a log came from",
                body: "A sitting records the version the controller was running. That is the join the whole thing turns on: without it, \"still lean\" and \"lean again after the change\" are the same sentence. With it, a clean run recorded on the tune from before the change is correctly not counted as evidence for it.",
                keys: "",
            },
            GuideEntry {
                title: "Nothing branches",
                body: "A tune is one thing on one controller. There is no merge, deliberately — merging two sets of engine settings is not something anyone should be offered.",
                keys: "",
            },
        ],
    },
    GuideSection {
        title: "Letting an assistant help",
        blurb: "A local API an AI assistant or a script can read the session through.",
        entries: &[
            GuideEntry {
                title: "Turn it on",
                body: "Tools ▸ Agent API. It is off until you do, and it asks before opening the socket. It listens on 127.0.0.1 and nowhere else, and there is no setting that changes that.",
                keys: "",
            },
            GuideEntry {
                title: "The token",
                body: "Every request needs one. It is written with the address into agent-api.json in your workspace folder, so a program can find it without you typing anything. Loopback keeps other machines out; it does not keep out other programs on this one, which is what the token is for.",
                keys: "",
            },
            GuideEntry {
                title: "What it can read",
                body: "What is connected, every channel and the job it does, the samples of any channel, the insights, the tune, the tables, and the project. Live data is pushed as the ECU produces it rather than polled, so it arrives at the controller's pace rather than the window's.",
                keys: "",
            },
            GuideEntry {
                title: "What it can change",
                body: "Nothing, unless you tick Allow agent writes — which clears itself the moment the ECU disconnects, because the same laptop meets a bench engine one afternoon and a car the next. Even then it can only move a setting or a table cell in working memory.",
                keys: "",
            },
            GuideEntry {
                title: "It can never burn",
                body: "Not an endpoint that refuses: there is none. Everything an assistant does is undone by turning the key off. Committing to flash stays with whoever can see the engine.",
                keys: "",
            },
            GuideEntry {
                title: "From Claude Code",
                body: "openlogviewer-mcp, in the tools folder, fronts the same API so the tools arrive named rather than as a port and a token. It finds both itself.",
                keys: "",
            },
        ],
    },
    GuideSection {
        title: "VE Calibration",
        blurb: "Suggesting a new fuel table from logged mixture against target.",
        entries: &[
            GuideEntry {
                title: "What it does",
                body: "In Histogram view, pick one of the tune's own tables under Axis breakpoints, set Compare against to the target channel, and tick Suggest a new fuel table. The reasoning is one line: the engine took in a known amount of air, the ECU metered fuel using the number in the cell, and the wideband says what came out. Richer than target means the ECU thought there was more air than there was.",
                keys: "",
            },
            GuideEntry {
                title: "What it refuses to do",
                body: "A cell with fewer than Min samples is left alone and counted as thin. A correction larger than Max change % is clamped rather than applied whole. Cells the log never visited are untouched, not zeroed. A zero or negative target is not a target, and those samples are skipped.",
                keys: "",
            },
            GuideEntry {
                title: "Thin cells move less",
                body: "Above the floor, a cell is trusted in proportion to its evidence rather than all at once — a cell with twelve samples and one with two hundred are not the same measurement. A thin cell stays near the number it already holds.",
                keys: "",
            },
            GuideEntry {
                title: "Wideband delay",
                body: "The reading at a given moment is not evidence about that moment: fuel metered now is burned, carried down the pipe to wherever the sensor is, and only then measured. Left uncorrected, every reading is credited to whatever the engine was doing a few hundred milliseconds too late — which through a fast ramp puts the correction in the wrong cell entirely.",
                keys: "",
            },
            GuideEntry {
                title: "Find the delay from the log",
                body: "Press Find it. Samples landing in one cell were taken at about the same operating point, so the mixtures they measured ought to agree; the delay that makes them agree best is the answer. It reports a band rather than a single number, because neighbouring candidates often sit within their own noise, and it refuses outright when the log holds no sharp changes to learn from.",
                keys: "Find it",
            },
            GuideEntry {
                title: "Read the result",
                body: "The summary says how many cells were suggested, how many were too thin, and the largest change. Show the new numbers switches between how far each cell moves and the values themselves. Export the table as CSV to paste into your tuning app.",
                keys: "",
            },
            GuideEntry {
                title: "Measure and target must be the same quantity",
                body: "AFR against a lambda target divides 12.5 by 0.9 and reports every cell as fifteen per cent lean — a full table of confident nonsense. That pairing is detected and refused rather than drawn.",
                keys: "",
            },
        ],
    },
    GuideSection {
        title: "Calculated channels",
        blurb: "Channels you define from the ones the log already has.",
        entries: &[
            GuideEntry {
                title: "Add one",
                body: "ƒ Add calculated channel in the sidebar. AFR - AFR Target 1, or RPM * Torque / 5252, or if(Boost psi > 0, Boost psi, 0). Once built they are ordinary channels: plottable, usable as an axis, available to filters and searches, and included in an export. They are marked ƒ in the list.",
                keys: "",
            },
            GuideEntry {
                title: "Writing an expression",
                body: "Channel names need no quoting even with spaces in them — names are matched longest first, so AFR Target 1 wins over AFR. Operators + - * / % ^, comparisons, && || !, and the functions abs sqrt min max clamp floor ceil round log log10 exp pow sign if. pi and e are constants.",
                keys: "",
            },
            GuideEntry {
                title: "Missing readings propagate",
                body: "Including through comparisons. Returning false for a reading that was never taken would let if choose a branch on the strength of nothing. A result that is not finite becomes a gap rather than an infinity, which would otherwise take the channel's range with it.",
                keys: "",
            },
            GuideEntry {
                title: "They travel",
                body: "Definitions are held by name and expression, so they apply to any log carrying those channels. One that does not fit the open log is reported rather than dropped.",
                keys: "",
            },
        ],
    },
    GuideSection {
        title: "Colours and scales",
        blurb: "Making a channel look the same in every log, and reading a noisy one.",
        entries: &[
            GuideEntry {
                title: "Smooth a noisy channel",
                body: "Right-click a channel row, then Smoothing: Light, Medium or Strong. A pressure or temperature sensor that arrives as fuzz becomes a line you can read the shape of. The setting is remembered against the channel like a colour is.",
                keys: "Right-click ▸ Smoothing",
            },
            GuideEntry {
                title: "It is a way of drawing, not of measuring",
                body: "Only the line changes. The insights, the VE calibration, the heat table, the statistics and every export read the channel exactly as it was logged — a smoothed mixture hides the single-sample lean excursion that damages a piston, so nothing that judges an engine is ever shown one. A smoothed channel is marked in its row so the two are never confused.",
                keys: "",
            },
            GuideEntry {
                title: "A median, not an average",
                body: "Sensor noise arrives as spikes, and an average smears each spike across its whole window — one bad sample in fifteen moving the line for fifteen samples is worse than the spike was. A median throws the spike away and keeps the level, and keeps edges: a real step from one pressure to another survives it.",
                keys: "",
            },
            GuideEntry {
                title: "Pin a colour",
                body: "Right-click a channel row, then Colour. The current scheme's palette is offered because those entries have been checked against this background for contrast and for separation under colour-vision deficiency.",
                keys: "Right-click a row",
            },
            GuideEntry {
                title: "What pinning a colour costs",
                body: "Trace colours are normally re-picked whenever the scheme changes, since a palette is only separable against the background it was chosen for. A pinned one is not re-picked and not re-checked — that is the point of pinning it — so a colour chosen on a dark scheme may sit poorly on a light one.",
                keys: "",
            },
            GuideEntry {
                title: "Pin a scale",
                body: "Fixed scale… draws a channel over a range you name instead of its own. Auto-scaling is what lets a dozen channels in different units share a plot, and it costs comparability: the same channel is drawn over a different range in every log. Pinning RPM to 0…8000 gives that back.",
                keys: "",
            },
            GuideEntry {
                title: "Units in the editor",
                body: "The boxes take and show their bounds in whichever units the list is showing, and say which. The pin itself is stored in the log's own units, so switching between metric and imperial redraws the labels without moving the range.",
                keys: "",
            },
            GuideEntry {
                title: "Back to automatic",
                body: "Unpins both the colour and the scale for that channel at once, so it takes whichever palette entry it is handed and its own range again. Pinned choices live in channels.json beside your presets and filters; deleting that file clears the lot.",
                keys: "",
            },
        ],
    },
    GuideSection {
        title: "Live connection",
        blurb: "Reading an ECU or a car as it runs.",
        entries: &[
            GuideEntry {
                title: "Connect",
                body: "Connect ▾ lists the serial ports. Pick one and the ECU is asked what it is, the matching definition is found, and a session starts. A live session is an ordinary log: the sidebar, filters, calculated channels, the heat table and VE Calibration all work on it exactly as they do on a file.",
                keys: "",
            },
            GuideEntry {
                title: "Reconnect",
                body: "Devices are remembered by hardware id rather than by COM port, because Windows hands port numbers out and reuses them. The toolbar carries a Connect: your ECU button for the last one used.",
                keys: "Ctrl+K",
            },
            GuideEntry {
                title: "The definition must match",
                body: "A session is refused when no definition matches the signature the ECU reports. Firmware versions move channels around inside the realtime block, so decoding with the wrong one does not fail — it reads every channel from the wrong offset and returns numbers that look entirely reasonable. Open the tune before connecting for the channels the firmware derives from tune settings.",
                keys: "",
            },
            GuideEntry {
                title: "Recording is yours to start",
                body: "Connecting does not record on its own — a session is opened to check a link or watch a gauge far more often than to capture anything. Press Record when you mean it. Each recording's clock starts where you pressed record, and every row is flushed as it arrives, so a pulled cable leaves a complete file.",
                keys: "",
            },
            GuideEntry {
                title: "Losing the link is not the end",
                body: "Key off and key on is normal, so a lost link is waited on for a minute — the indicator goes hollow and amber — and the session carries straight on into the same recording when the ECU comes back.",
                keys: "",
            },
            GuideEntry {
                title: "Reading is all it does",
                body: "Connecting sends only the commands that ask what the firmware is and read the realtime page and the settings. Nothing is written unless you edit a table and press the button.",
                keys: "",
            },
            GuideEntry {
                title: "Any OBD2 car",
                body: "Through an ELM327, with no definition file and nothing set up in advance: the standard fixes what every parameter means, and the car reports which ones it answers to. It is slow — around 2 Hz, because OBD2 has no realtime block and each parameter is its own request. Fine for watching a car, no use for catching a misfire.",
                keys: "",
            },
            GuideEntry {
                title: "Bluetooth and Wi-Fi dongles",
                body: "A Bluetooth LE adapter never becomes a COM port however long you wait, which is how a perfectly good dongle comes to look broken; those are listed in the same menu with (Bluetooth LE) after the name. A Wi-Fi dongle is its own access point and appears in no list at all — join its network first, check Windows has stayed on it, and use Connect to a Wi-Fi OBD2 adapter.",
                keys: "",
            },
            GuideEntry {
                title: "A generic cable",
                body: "An adapter Windows only describes as a USB-SERIAL CH340 is indistinguishable from a tuning cable until something talks to it, and the two want opposite opening moves. Use Connect ▾ → Connect as an OBD2 adapter for those.",
                keys: "",
            },
        ],
    },
    GuideSection {
        title: "Gauges",
        blurb: "The firmware's own dials over a live connection.",
        entries: &[
            GuideEntry {
                title: "Watch a connection",
                body: "Switch to Gauges. The dials are the ones the firmware defines, with its own ranges and its own warning bands where it declares them.",
                keys: "",
            },
            GuideEntry {
                title: "A dial with no face",
                body: "Some gauges are defined with their bounds left at zero. They are kept rather than dropped — the channel is worth seeing even when the firmware has not said what a normal value looks like — and show as a reading without a scale.",
                keys: "",
            },
            GuideEntry {
                title: "OBD2 dials",
                body: "Drawn to the standard's own ranges, with no warning bands: OBD2 describes what a value is and never what a safe one would be. The rev counter is drawn to 8,000 rather than the 16,383 the encoding permits, since there is no way to ask a car for its redline.",
                keys: "",
            },
        ],
    },
    GuideSection {
        title: "Editing a tune",
        blurb: "Changing a table on a connected ECU.",
        entries: &[
            GuideEntry {
                title: "Open a table",
                body: "Switch to Calibration on a live connection. The tables are read off the controller rather than from a saved file, so they are what it is running now.",
                keys: "",
            },
            GuideEntry {
                title: "Select and change",
                body: "Click or drag to pick a block of cells; arrows move, shift extends. + and − nudge by the firmware's own smallest step, shift for ten of them. Page Up and Page Down scale by 1%, shift for 5% — scaling because that is how tuning is actually done: a region four per cent lean is corrected by adding four per cent, not by typing sixteen numbers. Escape puts the selection back.",
                keys: "+ − PgUp PgDn Esc",
            },
            GuideEntry {
                title: "Nothing is sent until you say so",
                body: "A changed cell is outlined and the header counts them. The shading still says what the value is; the outline says it is not what the ECU holds. Send to ECU reports how many cells it is about to change — a table scaled by 5% when one cell was meant is 256 changes, and it looks identical to one change until it is counted.",
                keys: "",
            },
            GuideEntry {
                title: "Send and Burn are separate",
                body: "Because they are separate on the ECU. A write lands in working memory and takes effect immediately on a running engine — and is forgotten at the next power cycle, so a change that turns out to be wrong is undone by turning the key off. A burn commits it to flash and is permanent; do it with the engine stopped. Every write is read back and compared before it is called done.",
                keys: "",
            },
            GuideEntry {
                title: "Values are held to what the firmware allows",
                body: "Which is far tighter than the storage: an ignition table kept as a signed 16-bit tenth of a degree would accept ±3,276° as far as the encoding cares, while the firmware declares −10 to 90.",
                keys: "",
            },
        ],
    },
    GuideSection {
        title: "Fault codes",
        blurb: "Reading and clearing what a car is complaining about.",
        entries: &[
            GuideEntry {
                title: "Read them",
                body: "Tools ▸ Fault codes…, once connected to an OBD2 vehicle. All three lists are read, because they are three different statements: confirmed codes lit the lamp, pending ones were seen once and the car does not yet believe them, and permanent ones cannot be erased by anything but the controller.",
                keys: "",
            },
            GuideEntry {
                title: "Where a description is missing",
                body: "The manufacturer-specific ranges are the maker's to assign, so P1131 means one thing on a Ford and something unrelated on a Toyota. The window says so rather than guessing, because a plausible description of the wrong one is how somebody buys a sensor they did not need.",
                keys: "",
            },
            GuideEntry {
                title: "Clearing costs more than the codes",
                body: "It also clears the freeze frame — the one record of what the engine was doing when the fault occurred, and the most useful thing there is for an intermittent — along with the oxygen sensor results and the readiness monitors. A car cleared this morning cannot pass an emissions test this afternoon whatever its condition.",
                keys: "",
            },
        ],
    },
    GuideSection {
        title: "Calculators",
        blurb: "The arithmetic a tuner keeps a phone open for.",
        entries: &[
            GuideEntry {
                title: "Open them",
                body: "ƒ Calculators in the toolbar. Injector sizing and dead time, turbo and compressor matching, intercooler and water injection, cam timing, manifold and header geometry, gearing and tyre size, octane blending, drag strip correlations, running costs, and an estimate of power from a log.",
                keys: "",
            },
            GuideEntry {
                title: "They say what they assume",
                body: "The drag-strip figures are correlations fitted to real runs rather than physics — there is no term for traction, gearing, the air or the driver. Each calculator states what it is not modelling, because that is what decides whether its answer is any use to you.",
                keys: "",
            },
        ],
    },
    GuideSection {
        title: "Export",
        blurb: "Getting numbers and pictures out.",
        entries: &[
            GuideEntry {
                title: "What is offered follows the view",
                body: "In log view: plotted channels as CSV, all channels as CSV, or the plot as a PNG. In histogram view: the table, the sample counts, or the table as a PNG. In scatter view: the plotted points, each row carrying its index in the log, or the scatter as a PNG.",
                keys: "",
            },
            GuideEntry {
                title: "Mark a span first",
                body: "and the CSV covers only that span. The menu says which it is about to write.",
                keys: "",
            },
            GuideEntry {
                title: "The CSV opens again here",
                body: "Numbers are written invariant-culture, so a file written on a machine with a comma decimal separator opens everywhere, and each value is the shortest text that reads back as the same sample. A missing reading is an empty cell, so gaps in logging survive the round trip.",
                keys: "",
            },
            GuideEntry {
                title: "The table pastes into a tuning app",
                body: "Written in the shape a tuning table has — X across the top, Y down the side, highest row first. Cells never visited are left empty rather than written as zero, which would read as a measurement of nothing.",
                keys: "",
            },
        ],
    },
    GuideSection {
        title: "Appearance",
        blurb: "Schemes, and the reasoning behind them.",
        entries: &[
            GuideEntry {
                title: "Colour schemes",
                body: "Fourteen, in the box at the top right: two dark, two light, eight taken from editor themes, and two high contrast. The choice is remembered.",
                keys: "",
            },
            GuideEntry {
                title: "A scheme is more than the chrome",
                body: "It carries its own trace palette, and switching re-picks the colours of everything plotted — a palette is only separable against the background it was chosen for. Every palette here was checked against its own background for lightness range, contrast, and separation of neighbouring entries under protanopia and deuteranopia.",
                keys: "",
            },
            GuideEntry {
                title: "Why the tables are one hue",
                body: "A rainbow ramp has no inherent order — the eye cannot rank yellow against cyan — and it collapses under colour-vision deficiency and in greyscale. Lightness ranks unambiguously for everyone. Where a table shows a deviation it diverges about a near-background neutral instead, because polarity is not magnitude.",
                keys: "",
            },
        ],
    },
    GuideSection {
        title: "Where your files are",
        blurb: "What is kept, and where.",
        entries: &[
            GuideEntry {
                title: "Recordings and exports",
                body: "In a single folder named after the app, in your user profile: Logs for live recordings, Exports for anything written out. Export ▾ → Open the folder takes you there and Change the folder… moves it.",
                keys: "",
            },
            GuideEntry {
                title: "Why not My Documents",
                body: "It is redirected into OneDrive on most machines, which buries recordings a couple of levels deeper and uploads every one of them while it is still being written — a long session is tens of megabytes of continuous sync over whatever connection the car happens to be near.",
                keys: "",
            },
            GuideEntry {
                title: "Settings",
                body: "Separate, under AppData: your theme, presets, filters, calculated channels and pinned colours and scales. Those belong to the app; the folder above belongs to you. Nothing is ever written next to the program.",
                keys: "",
            },
            GuideEntry {
                title: "A new install is a blank slate",
                body: "None of it travels with the software. The filters offered when you open a log are generated from the channels that log has and arrive switched off, so opening a file never silently changes what a table counts.",
                keys: "",
            },
            GuideEntry {
                title: "Forget remembered ECUs",
                body: "Tools ▸ Forget remembered ECUs clears the device list and nothing else — presets, filters and calculated channels are your work and are not swept up in it.",
                keys: "",
            },
        ],
    },
];

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn there_are_sections() {
        assert!(!sections().is_empty());
    }

    #[test]
    fn every_section_has_a_title_a_blurb_and_entries() {
        for section in sections() {
            assert!(!section.title.trim().is_empty());
            assert!(!section.blurb.trim().is_empty());
            assert!(!section.entries.is_empty());
        }
    }

    #[test]
    fn every_entry_has_a_title_and_something_to_say() {
        for entry in all_entries() {
            assert!(!entry.title.trim().is_empty());
            assert!(!entry.body.trim().is_empty());
            // Long enough to be an explanation rather than a label.
            assert!(
                entry.body.encode_utf16().count() > 40,
                "\"{}\" says almost nothing",
                entry.title
            );
        }
    }

    fn distinct_ignoring_case(titles: Vec<&str>) -> bool {
        let n = titles.len();
        let mut folded: Vec<String> = titles.iter().map(|t| t.to_uppercase()).collect();
        folded.sort();
        folded.dedup();
        folded.len() == n
    }

    #[test]
    fn section_titles_are_distinct() {
        assert!(distinct_ignoring_case(sections().iter().map(|s| s.title).collect()));
    }

    #[test]
    fn entry_titles_are_distinct_within_a_section() {
        for section in sections() {
            assert!(distinct_ignoring_case(
                section.entries.iter().map(|e| e.title).collect()
            ));
        }
    }

    #[test]
    fn no_entry_is_left_with_a_placeholder() {
        for entry in all_entries() {
            assert!(!contains_ignore_case(entry.body, "TODO"));
            assert!(!contains_ignore_case(entry.body, "TBD"));
            assert!(!contains_ignore_case(entry.body, "Lorem"));
        }
    }

    /// Every feature this project claims should be findable by somebody who knows
    /// only what it is called. This is the test that fails when a feature is
    /// added and the guide is not.
    #[test]
    fn every_feature_is_described_somewhere() {
        for feature in [
            "scatter",
            "histogram",
            "VE Calibration",
            "wideband delay",
            "calculated channel",
            "preset",
            "filter",
            "export",
            "fault code",
            "calculator",
            "gauge",
            "burn",
            "OBD2",
            "Bluetooth",
            "Wi-Fi",
            "record",
            "axis breakpoint",
            "colour scheme",
            "fixed scale",
            "stacked",
            "shift-drag",
            "span",
        ] {
            assert!(all_entries().any(|e| e.matches(feature)), "{feature}");
        }
    }

    #[test]
    fn the_shortcuts_are_listed() {
        for keys in ["Ctrl+O", "Ctrl+F", "Ctrl+K"] {
            assert!(all_entries().any(|e| e.keys.contains(keys)), "{keys}");
        }
    }

    #[test]
    fn searching_matches_on_title_body_and_keys() {
        let entry = GuideEntry {
            title: "Open a log",
            body: "Drop a file onto the window.",
            keys: "Ctrl+O",
        };

        assert!(entry.matches("open"));
        assert!(entry.matches("OPEN")); // and without regard to case
        assert!(entry.matches("window"));
        assert!(entry.matches("Ctrl+O"));
        assert!(!entry.matches("histogram"));
    }

    #[test]
    fn an_entry_with_no_shortcut_says_so() {
        let plain = GuideEntry {
            title: "A",
            body: "B",
            keys: "",
        };
        let keyed = GuideEntry {
            title: "A",
            body: "B",
            keys: "Ctrl+O",
        };
        assert!(!plain.has_keys());
        assert!(keyed.has_keys());
    }

    #[test]
    fn all_entries_covers_every_section() {
        let total: usize = sections().iter().map(|s| s.entries.len()).sum();
        assert_eq!(total, all_entries().count());
    }
}
