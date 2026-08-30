namespace OpenLogViewer.Core;

/// <summary>One thing you can do, and what is worth knowing about doing it.</summary>
/// <param name="Title">What it is, as a person would ask for it.</param>
/// <param name="Body">How to do it, and why it behaves as it does where that is not obvious.</param>
/// <param name="Keys">Keyboard or mouse shortcut, where there is one.</param>
public sealed record GuideEntry(string Title, string Body, string Keys = "")
{
    public bool HasKeys => Keys.Length > 0;

    /// <summary>Whether a search for <paramref name="text"/> should turn this up.</summary>
    public bool Matches(string text) =>
        Title.Contains(text, StringComparison.OrdinalIgnoreCase)
        || Body.Contains(text, StringComparison.OrdinalIgnoreCase)
        || Keys.Contains(text, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A group of related entries.</summary>
public sealed record GuideSection(string Title, string Blurb, IReadOnlyList<GuideEntry> Entries);

/// <summary>
/// The manual, carried inside the application.
///
/// In here rather than behind a link because of where this gets used. The people
/// this is for plug a laptop into a car, often in a garage with no internet and
/// no phone signal, and "open the documentation on the web" is the wrong thing to
/// say at that moment — it is the same reasoning that makes the installer
/// self-contained.
///
/// Written as data rather than as a page of markup so that it can be searched,
/// re-themed, and checked by a test: there is one asserting that every section
/// has entries and that none of the text has been left empty, which is the
/// failure a hand-written help page actually has.
/// </summary>
public static class Guide
{
    public static IReadOnlyList<GuideSection> Sections { get; } =
    [
        new("Getting started",
            "Open a recording and get something on screen.",
        [
            new("Open a log",
                "Open log… in the toolbar, or drop a file onto the window. Nothing is assumed "
                + "from the extension — the content is examined instead, so a log renamed by a "
                + "browser or an email client still opens.",
                "Ctrl+O"),

            new("What it reads",
                "MegaSquirt and TunerStudio .mlg and .msl, rusEFI, MaxxECU's zipped logs, and "
                + "delimited text from MoTeC, Haltech, Link, AEM, ECUMaster, Holley, HP Tuners, "
                + "Speeduino and most anything else that exports CSV. The text reader works out "
                + "the delimiter, the encoding, the units row and the time base for itself, "
                + "including European decimal commas."),

            new("If a log will not open",
                "The message says what was wrong with the file rather than that something failed. "
                + "A format that is close but not handled is usually easy to add — the reader is "
                + "one file."),

            new("Three ways to read a recording",
                "Log draws the channels against time. Histogram bins them into a table shaped like "
                + "the one in your ECU. Scatter puts every sample at its own X and Y. They are the "
                + "same samples and the same settings; only the question differs."),
        ]),

        new("Reading the plot",
            "The channel list, the traces, and getting about the recording.",
        [
            new("Choose channels",
                "Tick them in the list on the left. Common plots the set most logs are read for in "
                + "one click; All and None do what they say."),

            new("Find a channel",
                "The box at the top of the list matches on name, units or category. Sort by "
                + "category, A–Z, or plotted-first."),

            new("Hide unused",
                "On by default. Logs routinely declare channels that never move — 98 of 179 in one "
                + "sample — and hiding them is what makes the rest findable. They are still "
                + "recorded and still exported."),

            new("Read a value",
                "Move the pointer over the plot and every row shows its value there. The trace "
                + "nearest the pointer thickens, its row highlights, and a card gives its value "
                + "plus its highest and lowest — and the moment each happened."),

            new("Jump to an extreme",
                "The ▲ and ▼ buttons on a channel row, or right-click it. Clicking the max or min "
                + "line in the hover card does the same. The zoom is kept, and the channel is "
                + "plotted first if it was not showing."),

            new("Zoom and pan",
                "Scroll to zoom at the pointer, drag to pan, double-click to fit the whole log. "
                + "Reset zoom in the View menu goes back to everything."),

            new("Mark a span",
                "Shift-drag across the plot. Every channel row switches to min … max and the "
                + "average over that span, and the hover card describes the span rather than the "
                + "whole log. Click the plot to clear it.",
                "Shift-drag"),

            new("Overlaid or stacked",
                "Overlaid traces each scale to their own range and read well for timing between "
                + "channels. Stacked gives each its own strip, which is what you want past about "
                + "four."),

            new("Gaps are drawn as gaps",
                "A paused-and-resumed log leaves a hole, and a straight line across it would read "
                + "as steady data that was never recorded. The pen lifts when the step between "
                + "samples exceeds ten times the log's usual interval."),

            new("Steady channels look steady",
                "A sensor holding almost still would otherwise have its last decimal place "
                + "stretched to fill the lane, and a pressure holding 12.0 within a tenth would be "
                + "drawn as a wall of noise. Turn it off in the View menu when a small drift is "
                + "exactly what you are chasing."),

            new("Save a selection",
                "Plot what you want and press + Save. The preset becomes a chip you can click to "
                + "restore it. Presets are held by channel name, so one saved on a log applies to "
                + "any other that shares those names. Right-click a chip to overwrite or delete."),
        ]),

        new("Finding a moment",
            "Jump to where something happened.",
        [
            new("Search the log",
                "Type a condition — RPM > 4500 && TPS > 80 — and every stretch of the drive that "
                + "met it is shaded. Enter steps forward through them, shift-Enter back, Escape "
                + "closes the bar.",
                "Ctrl+F"),

            new("It uses the calculated-channel syntax",
                "On purpose: anyone who has written one already knows how to write a search, and a "
                + "condition that proves useful can be pasted into a calculated channel or a "
                + "filter without translation."),

            new("Runs, not crossings",
                "A signal sitting near its threshold crosses it repeatedly, and reporting each "
                + "crossing would bury the one thing that happened. A brief dip below is bridged, "
                + "so consecutive matches are one finding."),

            new("What it will not claim",
                "A sample where a named channel has no reading is counted as could not be judged, "
                + "separately from the misses — a comparison against a reading that was never "
                + "taken is unanswerable rather than false. Filters still apply, since they say "
                + "which part of the drive is under consideration."),
        ]),

        new("The heat table",
            "Binning a drive into the shape of a tuning table.",
        [
            new("Build one",
                "Switch to Histogram. Pick any three channels: two axes and a value. Each cell "
                + "reduces the samples that landed in it by mean, min, max or count."),

            new("Use the tune's own axes",
                "Every .mlg carries the tune it was recorded with, so the ECU's own breakpoints "
                + "can be read straight out of the log. Pick one under Axis breakpoints and the "
                + "table lines up cell for cell with the one in TunerStudio. Uniform bins never "
                + "do, because ECU axes are tight at idle and wide up top."),

            new("Open a tune by hand",
                "Open tune… for a .msl or .csv, which carry none. For an .mlg it is usually the "
                + "wrong thing to do: VE Calibration scales the numbers that produced the logged "
                + "mixture, and a tune edited since the drive would have it scale numbers the "
                + "engine never ran."),

            new("Only the zoomed range",
                "Restricts the table to the window you zoomed to in the log view, so it can be "
                + "built from a single pull rather than the whole drive."),

            new("Colour by sample count",
                "Re-shades by how much data backs each cell — the quick way to see which parts of "
                + "the map the drive actually exercised."),

            new("Trace a cell back to the log",
                "Click a populated cell. The view switches to the plot, frames the samples that "
                + "made it, and marks them. An engine passes through the same cell many times, so "
                + "the samples are grouped into visits: the longest is framed, the rest are shaded, "
                + "and the count is reported. A cell averaged over twelve passes is a very "
                + "different thing from one sustained pull.",
                "Click a cell"),

            new("And the other way round",
                "Mark a span on the plot, switch to Histogram, and every cell that stretch passed "
                + "through is ringed. Those are the cells the pull is evidence about, and the ones "
                + "worth editing on the strength of it."),

            new("Data filters",
                "A table built from a whole drive averages warmup, overrun and idle into the same "
                + "cells as the pulls you care about. Each filter is a condition on a channel and "
                + "they combine with AND. Suggestions appear for the channels a log has and always "
                + "arrive switched off, so opening a file never silently changes what is counted."),

            new("What filtering does to the axes",
                "They re-scale to the samples that survive. Filtering to warm running also tightens "
                + "the RPM and load range onto that data, which is usually what you want — but it "
                + "means two differently-filtered tables are not comparable cell for cell."),
        ]),

        new("Scatter",
            "Every sample where it fell, coloured by a third channel.",
        [
            new("What it shows that a table cannot",
                "Spread. A cell reading dead on target looks the same whether it was measured "
                + "twelve times at target or six times rich and six times lean, and that "
                + "difference is most of what a log has to say about whether a region is settled "
                + "or merely averages well."),

            new("Marks are computed, not raced for",
                "A drive is tens of thousands of samples in a small part of the map. Drawn one "
                + "mark per sample they stack thousands deep and the surviving colour is whichever "
                + "was drawn last — an accident of log order. Samples are aggregated onto the "
                + "display's own grid instead, so every mark is the mean of what landed under it."),

            new("Hover a mark",
                "It reports the count behind it and the range its samples covered. The spread is "
                + "exactly what a mean hides, so it is said rather than left to be inferred."),

            new("The colour scale is trimmed",
                "A wideband touches both its rails during a transient, and scaled over the full "
                + "range those few blocks own the ramp while the drive draws as one flat colour. "
                + "The ends are trimmed and anything past them saturates — still drawn, still the "
                + "most extreme mark, still exact on hover. The legend marks a bound ≤ or ≥ only "
                + "when the trim moved it far enough to matter."),

            new("Click a mark",
                "Traces back to the log the same way a table cell does.",
                "Click a mark"),
        ]),

        new("VE Calibration",
            "Suggesting a new fuel table from logged mixture against target.",
        [
            new("What it does",
                "In Histogram view, pick one of the tune's own tables under Axis breakpoints, set "
                + "Compare against to the target channel, and tick Suggest a new fuel table. The "
                + "reasoning is one line: the engine took in a known amount of air, the ECU metered "
                + "fuel using the number in the cell, and the wideband says what came out. Richer "
                + "than target means the ECU thought there was more air than there was."),

            new("What it refuses to do",
                "A cell with fewer than Min samples is left alone and counted as thin. A correction "
                + "larger than Max change % is clamped rather than applied whole. Cells the log "
                + "never visited are untouched, not zeroed. A zero or negative target is not a "
                + "target, and those samples are skipped."),

            new("Thin cells move less",
                "Above the floor, a cell is trusted in proportion to its evidence rather than all "
                + "at once — a cell with twelve samples and one with two hundred are not the same "
                + "measurement. A thin cell stays near the number it already holds."),

            new("Wideband delay",
                "The reading at a given moment is not evidence about that moment: fuel metered now "
                + "is burned, carried down the pipe to wherever the sensor is, and only then "
                + "measured. Left uncorrected, every reading is credited to whatever the engine was "
                + "doing a few hundred milliseconds too late — which through a fast ramp puts the "
                + "correction in the wrong cell entirely."),

            new("Find the delay from the log",
                "Press Find it. Samples landing in one cell were taken at about the same operating "
                + "point, so the mixtures they measured ought to agree; the delay that makes them "
                + "agree best is the answer. It reports a band rather than a single number, "
                + "because neighbouring candidates often sit within their own noise, and it "
                + "refuses outright when the log holds no sharp changes to learn from.",
                "Find it"),

            new("Read the result",
                "The summary says how many cells were suggested, how many were too thin, and the "
                + "largest change. Show the new numbers switches between how far each cell moves "
                + "and the values themselves. Export the table as CSV to paste into your tuning "
                + "app."),

            new("Measure and target must be the same quantity",
                "AFR against a lambda target divides 12.5 by 0.9 and reports every cell as fifteen "
                + "per cent lean — a full table of confident nonsense. That pairing is detected and "
                + "refused rather than drawn."),
        ]),

        new("Calculated channels",
            "Channels you define from the ones the log already has.",
        [
            new("Add one",
                "ƒ Add calculated channel in the sidebar. AFR - AFR Target 1, or RPM * Torque / "
                + "5252, or if(Boost psi > 0, Boost psi, 0). Once built they are ordinary channels: "
                + "plottable, usable as an axis, available to filters and searches, and included in "
                + "an export. They are marked ƒ in the list."),

            new("Writing an expression",
                "Channel names need no quoting even with spaces in them — names are matched longest "
                + "first, so AFR Target 1 wins over AFR. Operators + - * / % ^, comparisons, && || "
                + "!, and the functions abs sqrt min max clamp floor ceil round log log10 exp pow "
                + "sign if. pi and e are constants."),

            new("Missing readings propagate",
                "Including through comparisons. Returning false for a reading that was never taken "
                + "would let if choose a branch on the strength of nothing. A result that is not "
                + "finite becomes a gap rather than an infinity, which would otherwise take the "
                + "channel's range with it."),

            new("They travel",
                "Definitions are held by name and expression, so they apply to any log carrying "
                + "those channels. One that does not fit the open log is reported rather than "
                + "dropped."),
        ]),

        new("Colours and scales",
            "Making a channel look the same in every log.",
        [
            new("Pin a colour",
                "Right-click a channel row, then Colour. The current scheme's palette is offered "
                + "because those entries have been checked against this background for contrast "
                + "and for separation under colour-vision deficiency.",
                "Right-click a row"),

            new("What pinning a colour costs",
                "Trace colours are normally re-picked whenever the scheme changes, since a palette "
                + "is only separable against the background it was chosen for. A pinned one is not "
                + "re-picked and not re-checked — that is the point of pinning it — so a colour "
                + "chosen on a dark scheme may sit poorly on a light one."),

            new("Pin a scale",
                "Fixed scale… draws a channel over a range you name instead of its own. "
                + "Auto-scaling is what lets a dozen channels in different units share a plot, and "
                + "it costs comparability: the same channel is drawn over a different range in "
                + "every log. Pinning RPM to 0…8000 gives that back."),

            new("Units in the editor",
                "The boxes take and show their bounds in whichever units the list is showing, and "
                + "say which. The pin itself is stored in the log's own units, so switching between "
                + "metric and imperial redraws the labels without moving the range."),

            new("Back to automatic",
                "Unpins both the colour and the scale for that channel at once, so it takes "
                + "whichever palette entry it is handed and its own range again. Pinned choices "
                + "live in channels.json beside your presets and filters; deleting that file "
                + "clears the lot."),
        ]),

        new("Live connection",
            "Reading an ECU or a car as it runs.",
        [
            new("Connect",
                "Connect ▾ lists the serial ports. Pick one and the ECU is asked what it is, the "
                + "matching definition is found, and a session starts. A live session is an "
                + "ordinary log: the sidebar, filters, calculated channels, the heat table and VE "
                + "Calibration all work on it exactly as they do on a file."),

            new("Reconnect",
                "Devices are remembered by hardware id rather than by COM port, because Windows "
                + "hands port numbers out and reuses them. The toolbar carries a Connect: your ECU "
                + "button for the last one used.",
                "Ctrl+K"),

            new("The definition must match",
                "A session is refused when no definition matches the signature the ECU reports. "
                + "Firmware versions move channels around inside the realtime block, so decoding "
                + "with the wrong one does not fail — it reads every channel from the wrong offset "
                + "and returns numbers that look entirely reasonable. Open the tune before "
                + "connecting for the channels the firmware derives from tune settings."),

            new("Recording is yours to start",
                "Connecting does not record on its own — a session is opened to check a link or "
                + "watch a gauge far more often than to capture anything. Press Record when you "
                + "mean it. Each recording's clock starts where you pressed record, and every row "
                + "is flushed as it arrives, so a pulled cable leaves a complete file."),

            new("Losing the link is not the end",
                "Key off and key on is normal, so a lost link is waited on for a minute — the "
                + "indicator goes hollow and amber — and the session carries straight on into the "
                + "same recording when the ECU comes back."),

            new("Reading is all it does",
                "Connecting sends only the commands that ask what the firmware is and read the "
                + "realtime page and the settings. Nothing is written unless you edit a table and "
                + "press the button."),

            new("Any OBD2 car",
                "Through an ELM327, with no definition file and nothing set up in advance: the "
                + "standard fixes what every parameter means, and the car reports which ones it "
                + "answers to. It is slow — around 2 Hz, because OBD2 has no realtime block and "
                + "each parameter is its own request. Fine for watching a car, no use for catching "
                + "a misfire."),

            new("Bluetooth and Wi-Fi dongles",
                "A Bluetooth LE adapter never becomes a COM port however long you wait, which is "
                + "how a perfectly good dongle comes to look broken; those are listed in the same "
                + "menu with (Bluetooth LE) after the name. A Wi-Fi dongle is its own access point "
                + "and appears in no list at all — join its network first, check Windows has "
                + "stayed on it, and use Connect to a Wi-Fi OBD2 adapter."),

            new("A generic cable",
                "An adapter Windows only describes as a USB-SERIAL CH340 is indistinguishable from "
                + "a tuning cable until something talks to it, and the two want opposite opening "
                + "moves. Use Connect ▾ → Connect as an OBD2 adapter for those."),
        ]),

        new("Gauges",
            "The firmware's own dials over a live connection.",
        [
            new("Watch a connection",
                "Switch to Gauges. The dials are the ones the firmware defines, with its own ranges "
                + "and its own warning bands where it declares them."),

            new("A dial with no face",
                "Some gauges are defined with their bounds left at zero. They are kept rather than "
                + "dropped — the channel is worth seeing even when the firmware has not said what a "
                + "normal value looks like — and show as a reading without a scale."),

            new("OBD2 dials",
                "Drawn to the standard's own ranges, with no warning bands: OBD2 describes what a "
                + "value is and never what a safe one would be. The rev counter is drawn to 8,000 "
                + "rather than the 16,383 the encoding permits, since there is no way to ask a car "
                + "for its redline."),
        ]),

        new("Editing a tune",
            "Changing a table on a connected ECU.",
        [
            new("Open a table",
                "Switch to Calibration on a live connection. The tables are read off the "
                + "controller rather than from a saved file, so they are what it is running now."),

            new("Select and change",
                "Click or drag to pick a block of cells; arrows move, shift extends. + and − nudge "
                + "by the firmware's own smallest step, shift for ten of them. Page Up and Page "
                + "Down scale by 1%, shift for 5% — scaling because that is how tuning is actually "
                + "done: a region four per cent lean is corrected by adding four per cent, not by "
                + "typing sixteen numbers. Escape puts the selection back.",
                "+ − PgUp PgDn Esc"),

            new("Nothing is sent until you say so",
                "A changed cell is outlined and the header counts them. The shading still says what "
                + "the value is; the outline says it is not what the ECU holds. Send to ECU reports "
                + "how many cells it is about to change — a table scaled by 5% when one cell was "
                + "meant is 256 changes, and it looks identical to one change until it is counted."),

            new("Send and Burn are separate",
                "Because they are separate on the ECU. A write lands in working memory and takes "
                + "effect immediately on a running engine — and is forgotten at the next power "
                + "cycle, so a change that turns out to be wrong is undone by turning the key off. "
                + "A burn commits it to flash and is permanent; do it with the engine stopped. "
                + "Every write is read back and compared before it is called done."),

            new("Values are held to what the firmware allows",
                "Which is far tighter than the storage: an ignition table kept as a signed 16-bit "
                + "tenth of a degree would accept ±3,276° as far as the encoding cares, while the "
                + "firmware declares −10 to 90."),
        ]),

        new("Fault codes",
            "Reading and clearing what a car is complaining about.",
        [
            new("Read them",
                "Tools ▸ Fault codes…, once connected to an OBD2 vehicle. All three lists are read, "
                + "because they are three different statements: confirmed codes lit the lamp, "
                + "pending ones were seen once and the car does not yet believe them, and permanent "
                + "ones cannot be erased by anything but the controller."),

            new("Where a description is missing",
                "The manufacturer-specific ranges are the maker's to assign, so P1131 means one "
                + "thing on a Ford and something unrelated on a Toyota. The window says so rather "
                + "than guessing, because a plausible description of the wrong one is how somebody "
                + "buys a sensor they did not need."),

            new("Clearing costs more than the codes",
                "It also clears the freeze frame — the one record of what the engine was doing when "
                + "the fault occurred, and the most useful thing there is for an intermittent — "
                + "along with the oxygen sensor results and the readiness monitors. A car cleared "
                + "this morning cannot pass an emissions test this afternoon whatever its "
                + "condition."),
        ]),

        new("Calculators",
            "The arithmetic a tuner keeps a phone open for.",
        [
            new("Open them",
                "ƒ Calculators in the toolbar. Injector sizing and dead time, turbo and compressor "
                + "matching, intercooler and water injection, cam timing, manifold and header "
                + "geometry, gearing and tyre size, octane blending, drag strip correlations, "
                + "running costs, and an estimate of power from a log."),

            new("They say what they assume",
                "The drag-strip figures are correlations fitted to real runs rather than physics — "
                + "there is no term for traction, gearing, the air or the driver. Each calculator "
                + "states what it is not modelling, because that is what decides whether its answer "
                + "is any use to you."),
        ]),

        new("Export",
            "Getting numbers and pictures out.",
        [
            new("What is offered follows the view",
                "In log view: plotted channels as CSV, all channels as CSV, or the plot as a PNG. "
                + "In histogram view: the table, the sample counts, or the table as a PNG. In "
                + "scatter view: the plotted points, each row carrying its index in the log, or the "
                + "scatter as a PNG."),

            new("Mark a span first",
                "and the CSV covers only that span. The menu says which it is about to write."),

            new("The CSV opens again here",
                "Numbers are written invariant-culture, so a file written on a machine with a comma "
                + "decimal separator opens everywhere, and each value is the shortest text that "
                + "reads back as the same sample. A missing reading is an empty cell, so gaps in "
                + "logging survive the round trip."),

            new("The table pastes into a tuning app",
                "Written in the shape a tuning table has — X across the top, Y down the side, "
                + "highest row first. Cells never visited are left empty rather than written as "
                + "zero, which would read as a measurement of nothing."),
        ]),

        new("Appearance",
            "Schemes, and the reasoning behind them.",
        [
            new("Colour schemes",
                "Fourteen, in the box at the top right: two dark, two light, eight taken from "
                + "editor themes, and two high contrast. The choice is remembered."),

            new("A scheme is more than the chrome",
                "It carries its own trace palette, and switching re-picks the colours of everything "
                + "plotted — a palette is only separable against the background it was chosen for. "
                + "Every palette here was checked against its own background for lightness range, "
                + "contrast, and separation of neighbouring entries under protanopia and "
                + "deuteranopia."),

            new("Why the tables are one hue",
                "A rainbow ramp has no inherent order — the eye cannot rank yellow against cyan — "
                + "and it collapses under colour-vision deficiency and in greyscale. Lightness "
                + "ranks unambiguously for everyone. Where a table shows a deviation it diverges "
                + "about a near-background neutral instead, because polarity is not magnitude."),
        ]),

        new("Where your files are",
            "What is kept, and where.",
        [
            new("Recordings and exports",
                "In a single folder named after the app, in your user profile: Logs for live "
                + "recordings, Exports for anything written out. Export ▾ → Open the folder takes "
                + "you there and Change the folder… moves it."),

            new("Why not My Documents",
                "It is redirected into OneDrive on most machines, which buries recordings a couple "
                + "of levels deeper and uploads every one of them while it is still being written — "
                + "a long session is tens of megabytes of continuous sync over whatever connection "
                + "the car happens to be near."),

            new("Settings",
                "Separate, under AppData: your theme, presets, filters, calculated channels and "
                + "pinned colours and scales. Those belong to the app; the folder above belongs to "
                + "you. Nothing is ever written next to the program."),

            new("A new install is a blank slate",
                "None of it travels with the software. The filters offered when you open a log are "
                + "generated from the channels that log has and arrive switched off, so opening a "
                + "file never silently changes what a table counts."),

            new("Forget remembered ECUs",
                "Tools ▸ Forget remembered ECUs clears the device list and nothing else — presets, "
                + "filters and calculated channels are your work and are not swept up in it."),
        ]),
    ];

    /// <summary>Every entry, for searching across sections.</summary>
    public static IEnumerable<GuideEntry> AllEntries => Sections.SelectMany(s => s.Entries);
}
