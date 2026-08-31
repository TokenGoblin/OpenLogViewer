# Changelog

## Unreleased

Everything below landed in one run of work. Grouped by what it does for you
rather than by commit.

### Housekeeping

**Third-party notices, and the installer now carries them.** The published build
is self-contained — the .NET runtime and WPF are inside the executable, all MIT,
and MIT asks that its notice travels with copies of the software. Setup displayed
a licence pane and installed nothing, which is not the same thing.
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) lists what is redistributed and
what is only used to build, and both it and the licence are now installed beside
the application.

It also records what is deliberately absent. No copyleft code or data is included
anywhere: RomRaider (GPL-2.0) and FreeSSM (GPL-3.0) both publish Subaru address
maps, neither is copied, and neither was read while implementing the protocol.
The two addresses in the SSM template were measured against a running car and
cross-checked against OBD2 before either was consulted — the probe transcript and
the commit order both record it.

**Continuous integration.** There are 1,440 tests and nothing had been running
them except somebody remembering to. Every push and pull request now restores,
builds with warnings as errors, runs the suite, and fails on a dependency with a
known advisory.

**A clean build at last.** Four analyser warnings and three compiler warnings are
gone. Two were worth more than the silence: `MathExpression.TryParse` did not
declare that it returns a non-null expression when it succeeds, so every caller
carried a nullable it could not have been null, and a text box named `SetValue`
was hiding `DependencyObject.SetValue` on the main window — harmless until
somebody called it.

### Added

**Insights — what a datalog says about the engine that produced it.** Thirteen
findings, measured rather than eyeballed: fuelling against target, lean
excursions under load, knock retard, closed-loop bias, injector duty, charging,
warmup, idle steadiness, sensors that never moved. Each carries the arithmetic it
rests on, so it can be argued with. Levels run Warning, Watch, Note, Good and
Not measured — the last two on purpose, because an analysis that only ever
complains cannot tell you anything is right, and one that guesses when a log
cannot answer is worse than one that says so.

**The tuning project — what you tried and whether it worked.** One project per
vehicle. Recording a log keeps every finding and raises a fix for anything newly
warned about; the same fault seen again is noted against the fix already open
rather than raising a second, so the record shows whether a change is working.
Copy puts the whole thing on the clipboard as plain text, which is what to paste
to an assistant. **Tools ▸ Tuning project.**

**Version control for tunes.** A version is a tune kept with *why* — what it was
for, which fixes it addresses, what it came from, whether it reached flash — as
an ordinary `.msq` TunerStudio still opens. Identity is the bytes, so reading an
unchanged tune twice gives one version and burning a recorded one is news about
it rather than a new one. Versions are compared setting by setting, not byte by
byte. Nothing branches: a tune is one thing on one controller, and merging two
sets of engine settings is not something anyone should be offered.

And the join that makes it worth having: **a sitting records which version the
ECU was running.** Without it "still lean" and "lean again after the change" are
the same sentence.

**A local API an AI assistant can watch through.** HTTP for questions, one
WebSocket for live data pushed at the ECU's pace rather than the window's.
Loopback only, with a token in your workspace. It changes nothing unless you tick
Allow agent writes — which clears itself on disconnect — and it can never burn:
not an endpoint that refuses, none at all. `openlogviewer-mcp` fronts the same
API for Claude Code. **Tools ▸ Agent API.**

**`olv-insights` — the analysis with no window.** One log or a folder, no ECU and
no network, exit code 0/1/2 for nothing-wrong / worth-watching / warning so a
script can act on a drive without parsing anything. `--project` records what it
found.

**Smoothing on the channel menu.** Light, Medium or Strong, a median rather than
an average so spikes are discarded instead of smeared and real edges survive.
Display only — the insights, VE calibration, table, statistics and exports all
read the channel as logged, and a smoothed row says so.


**Scatter mode — a third reading of a recording.** The plot orders samples by
time; the heat table throws time away and bins by two channels. This throws time
away and does not bin: every sample at its own X and Y, coloured by a third
channel. It takes the same three channels the table does, through the same
filters and the same panel, so switching between them is a switch of view rather
than of setup.

What it shows that a table cannot is spread. A cell reading dead on target looks
identical whether it was measured twelve times at target or six times rich and
six times lean, and that difference is most of what a log has to say about
whether a region of the map is settled or merely averages well.

**Overplotting is what makes a naive scatter lie, so the marks are aggregated
before anything is drawn.** A drive is tens or hundreds of thousands of samples
concentrated in a small part of the map; drawn one mark per sample they stack
thousands deep and the colour that survives is whichever was drawn last — an
accident of the order the log is in, wearing the authority of a measurement.
Samples are binned onto the display's own grid at three pixels a block instead,
so every mark is the mean of what landed under it, and hovering one reports the
count behind it and the range its samples covered.

**The colour scale is trimmed rather than fitted to the extremes.** A wideband
touches both rails for a moment during a transient; scaled over the full range
those few blocks own the ramp and the drive around them draws as one flat
colour. The ends are trimmed to the 2nd and 98th percentiles of the occupied
blocks and anything past them saturates — still drawn, still the most extreme
mark, still exact on hover. The legend marks a bound `≤` or `≥` only when the
trim moved it far enough to matter, so a number that is not the largest value on
the plot is never read as one, and a channel with no outliers is not trimmed at
all.

Clicking a mark traces it back to the log the way a table cell does, grouping
the samples into visits and framing the longest. Export offers the points as
CSV, carrying each sample's index in the log, and the scatter as a PNG.

**VE Calibration now accounts for the wideband reading late, and trusts a cell in
proportion to its evidence.** Two corrections to what the analysis was doing,
both of which changed the numbers it suggests.

The reading at a given moment was being treated as evidence about that moment.
It is not: fuel metered on this revolution is burned, pushed down the pipe to
wherever the sensor is, and only then measured. Every reading was credited to
whatever the engine was doing a few hundred milliseconds too late — harmless at
steady state, and through a fast ramp the reason a correction landed in the wrong
cell and smeared across a region of the table. *Wideband delay* takes that time
in seconds and shifts the measurement, and only the measurement: the target is
what the ECU was aiming for when it metered the fuel, so it belongs to the same
moment as the cell. It defaults to none, because the figure depends on where the
sensor is fitted and nothing here can know that.

**But the log knows, and there is a button that asks it.** Samples landing in one
cell were taken at about the same operating point, so the mixtures they measured
ought to agree; pair a cell with readings taken too early or too late and
readings from neighbouring conditions leak in and its samples disagree. *Find it*
sweeps the delay and takes the one where they agree best. It is alignment rather
than tuning — every cell is judged against its own readings, never against a
target — so a badly mistuned engine aligns as well as a well tuned one.

It answers with a band rather than a point, because a sweep does not come to one:
neighbouring candidates routinely sit within their own sampling error, and naming
the lowest claims a precision the log does not carry. And it refuses outright
where it should — an engine held at one operating point gives every delay the
same score, which is the absence of evidence rather than a small delay, and
saying so is the only honest answer. Neither MTune nor MegaLogViewer measures
this; both ask you to guess.

And above the minimum-samples floor, a cell used to get the whole correction the
instant it cleared the threshold — a cell with twelve samples and one with two
hundred moved identically, though one is a measurement and the other a glance.
The correction is scaled by `n / (n + Min samples)` instead, so a thin cell stays
near the number it already holds. The floor itself is unchanged; this softens the
cliff above it rather than lowering it.

**The manual is in the application.** A Guide button beside Log, Gauges and
Calibration: sixteen sections covering every feature, searchable across all of
them at once, with keyboard shortcuts shown against the things they do.

In here rather than behind a link because of where this gets used. The people
this is for plug a laptop into a car, often in a garage with no internet and no
phone signal, and "open the documentation on the web" is the wrong thing to say
at that moment — the same reasoning that makes the installer self-contained. Help
▸ Documentation online is still there for anyone who has a connection.

Written as data rather than as a page of markup, so it is searchable, follows the
colour scheme, and can be checked by a test: there are tests that every section
has entries, that no entry is a placeholder or too thin to be an explanation, and
that every feature this project claims is findable by the name a user would search
for. That last one is what fails when a feature is added and the guide is not.

**The settings that are not tables can now be edited, and it has been done on a
real ECU.** Calibration has a Settings half beside the tables, its pages built
from the menus, dialogs and fields the firmware declares — 144 on an MS3, 55 on
an MS2Extra, 49 on a Speeduino. Fields appear and disappear as the conditions
the firmware wrote against its own settings become true.

Verified against a Speeduino 202501 over serial. The tune reads in 424 ms and
twice identically; decoded values check against the raw bytes on the wire.
Writing one RPM setting sent a single byte and left the other 3,407 untouched,
and flipping one of the seven bit fields sharing byte 83 moved exactly one bit
and left its six neighbours alone — which is the failure the whole design exists
to avoid, since an ECU takes a clobbered byte without complaint.

**Find a moment in the log.** Ctrl+F, a condition in the same syntax as a
calculated channel — `RPM > 4500 && TPS > 80` — and every stretch of the drive
that met it is shaded, with Enter and shift-Enter stepping through them.

The same syntax on purpose: anyone who has written a calculated channel already
knows how to write a search, and a condition that proves useful can be pasted
into a channel or a filter without translation. What it adds over a filter is
*where*: a filter answers which samples to count and throws the rest away, this
answers which moments to go and look at. Filters still narrow it, since they say
which part of the drive is under consideration.

Consecutive matches are one finding rather than fifty — a signal sitting near its
threshold crosses it repeatedly, and a brief dip below is bridged the way a
table cell's visits are. A sample where a named channel has no reading is counted
as *could not be judged* rather than as a miss, because a comparison against a
reading that was never taken is unanswerable and not false.

**Mark a pull on the plot, and the table rings the cells it reached.** The other
direction of the trace-back. Clicking a cell has always framed the samples that
built it; this answers the question asked first — mark a span, switch to
Histogram, and every cell that stretch of the drive passed through is outlined,
with the count in the status line. Those are the cells the pull is evidence
about, and the ones worth editing on the strength of it.

The two compose, so the round trip closes: a cell traced back to its longest
visit leaves that visit marked, and switching back rings the cell it came from.
Samples a filter excluded are counted as outside rather than marked — the table
does not rest on them — and a span landing mostly outside the table says so
rather than quietly marking almost nothing.

**A log that carries a tune this cannot read now says so.** A MaxxECU log is a
zip holding the tune that was running, in MTune's own format. Nothing here can
read it, and the app used to report the log as carrying no tune at all and
suggest opening a `.msq` — a file a MaxxECU owner does not have and cannot
produce. It now says the tune is there, that this cannot read it, and that axis
breakpoints and VE Calibration are unavailable for the log because of it. The
format is not decoded and is not guessed at: decoding it wrong would not fail,
it would produce plausible breakpoints and a VE suggestion resting on them.

**A channel can keep its own colour and its own scale.** Right-click a channel
row. Both are held by name in `channels.json`, so a choice made on one log
applies to every other carrying that channel.

The editor takes and shows its bounds in whichever units the list is showing,
and says which — the axis beside it reads °F, so the boxes do too — while the
pin itself is stored in the log's own units, so it means the same range whatever
system is chosen later.

The fixed scale is the more useful half. Auto-scaling every trace to its own
range is what lets a dozen channels in different units share a plot, and it
costs comparability: the same channel is drawn over a different range in every
log, and in the same log before and after a filter, so two runs cannot be read
against each other by eye. Pinning RPM to 0…8000 gives that back. A pinned range
is used exactly as given — the steady-channel floor is not applied on top of one,
since naming a range answers the question that floor exists to ask — and the
stacked-lane labels now report the range a lane is drawn over rather than the
channel's own extremes, which were not the same number even before this.

A pinned colour opts out of something and the menu is built to say so. Trace
colours are otherwise re-picked for every scheme, because a palette is only
separable against the background it was chosen for; a pinned one is not
re-picked and not re-checked. So the menu offers the current scheme's own
palette, whose entries have been checked against that background, and an entry a
pinned channel holds is no longer handed out to another trace.

**Live connection to a MegaSquirt.** *Connect ▾* lists the serial ports; picking
one reads the ECU's signature, matches an INI to it, and starts a live session.
A live session is an ordinary log, so the sidebar, filters, calculated channels,
the heat table and VE Calibration all work on it as they do on a file — and
channels take the names recorded logs use, so presets and filters transfer.

The INI is matched to the signature the ECU reports and a session is refused
when none matches. Firmware versions move channels inside the realtime block, so
the wrong INI does not fail — it reads every channel from the wrong offset and
returns numbers that look reasonable.

Losing the link does not end the session: key off and key on is normal, so a
lost link is waited on, and a recording in progress carries straight on into the
same file when the ECU returns.

A live session itself only reads: the commands it sends ask what the firmware is
and read the realtime page. Changing what the ECU is running is a separate and
deliberate act from the tune table editor — see *Sending a tune to the ECU*.

Verified against a MegaSquirt 3 on a bench: 249 channels at about 16 Hz with no
retries, surviving repeated unplugs.

**Any OBD2 car, through an ELM327 dongle.** The one connection here that needs
nothing known in advance: the parameter numbering, the scaling and the units are
the same on every OBD2 vehicle by law, and the car itself reports which
parameters it answers to — so a connection produces named, scaled channels on a
car nobody has ever plugged this into. What it costs is speed. OBD2 has no
realtime block, so each parameter is its own request and a row of readings takes
a good part of a second. Fine for watching a car; no use for catching a misfire.

Both radios, because these dongles split between them and the difference is
invisible until it fails. A Bluetooth Classic adapter pairs as a serial port and
appears as a COM port; a Bluetooth LE one never becomes a COM port however long
you wait, and is reached over GATT instead. Adapters are recognised by the name
they advertise, which is the only clue either radio offers — and the list knows
that an OBDLink advertises as "ScanTool.net-…", after the company rather than
the product, because a dongle that goes unrecognised gets probed as a MegaSquirt
and reported as an unknown ECU.

Adapters are named by what they are rather than by what they claim. All of them
answer `ATI` with an ELM327 version whether or not that is true; the STN chips
inside an OBDLink answer `STDI` and `STI` as well, so those report as "OBDLink
r2.6 (STN1100 v2.2.2)" and a clone that refuses both still reports its ELM327
version as before.

Verified on a live vehicle with two dongles: a BLE `OBDII` clone, and an OBDLink
r2.6 over Bluetooth Classic — 24 channels, connected in eight seconds, the car
settling on ISO 15765-4 with two modules answering.

**Wi-Fi dongles too — a Vgate iCar Pro and the ones built like it.** The third
radio these come with, and the one that hides best: a Wi-Fi adapter is its own
access point with a TCP socket behind it, so it becomes no COM port, pairs with
nothing, and there is no list for it to be missing from. It carries the same
ELM327 conversation as the other two, so everything above applies to it
unchanged — the capability walk, the fault codes, the gauges.

Reached by address, because there is nothing else to reach it by. *Connect ▾ →
Connect to a Wi-Fi OBD2 adapter* offers `192.168.0.10:35000`, which is where a
Vgate answers, and `192.168.4.1:35000` for the clones that differ;
`--connect-wifi <address>` takes any other, and `olv-probe --wifi auto` reaches
one from the probe.

What has to be true is not visible from inside the application: this computer
must have joined the dongle's network — `V-LINK` on a Vgate — and must still be
on it. Windows treats a network with no internet as a mistake and returns to one
that has some, often within seconds, so the failure appears at the dongle while
the cause is a laptop that went home. Nothing answering therefore says that,
along with the other two reasons: the adapters take one client at a time, so a
phone app still holding one is refused rather than queued, and the address itself
may simply be a different one.

**Not yet verified against the dongle from here.** The endpoint and the
adapter's habits are known from the same hardware on a 2014 Subaru, through a
different client; this path is checked against a fake adapter on a real socket,
including the one habit that breaks clients — a Vgate acknowledges `ATE0` and
goes on echoing anyway, so every reply arrives behind the command that caused it.

**A reply is finished when the adapter has finished, not when it says so.** The
`>` prompt is supposed to end every reply and is the only thing this waited for.
It is not reliable: on a Vgate it arrives on roughly 60–80 % of reads, and the
rest ran the whole window out with a complete answer already in the buffer. No
timeout is long enough for a character that is not coming — measured elsewhere,
lengthening the window made it worse — so quiet now finishes a reply too.

Which needs two guards, because the naive version of that rule breaks two other
things. **The echo does not count as an answer:** this adapter echoes, pauses,
and only then replies, so a pause after the echo would complete the read before
the reply existed and leave every answer one command late for the rest of the
session. And **a prompt with nothing before it is a leftover, not an answer** —
finishing on quiet leaves the previous reply's prompt still in flight, where it
cannot be discarded because it has not been sent, so it lands at the front of
the next read. Taken at face value it is a complete empty answer, which is the
same one-behind desync arriving by the other door. A read that receives nothing
at all still waits out its whole timeout: silence is not a short reply.

Around those: after a read that did time out, the next command waits for the
late answer and discards it rather than reading it as its own; the protocol
search is spent on one request whose answer is thrown away, so no capability
query is ever answered with "SEARCHING..." half-arrived; and reconnection now
backs off — doubling to a ceiling instead of a flat 750 ms — because these
dongles take one client at a time and a connect-and-reset every three-quarters
of a second is how you wedge one.

None of that is guesswork about a device nobody has: each rule is a defect
measured on this dongle against a running car, and each has a test that goes red
when the fix is taken back out.

**Six parameters to a request, where the car allows it.** The cost of OBD2 is
round trips rather than bytes — one request, one answer, one parameter — and ISO
15765 lets a single mode 01 request carry six. A round of readings is now two
exchanges instead of six, and the slow parameters stop being a queue: they used
to take turns one per round, and six now come back for the same single request.

Probed rather than assumed, and the probe can only answer yes on evidence. **Two
parameters must come back**: a car that ignores the extras and answers the first
one listed looks exactly like a batched reply that carried one, so a single
answer proves nothing. It is tried only on a bus **positively identified as
CAN** — "not identified as slow" is not the same test, an unknown protocol is
neither, and an asleep ECU answering `STOPPED` is precisely how a link comes to
be unidentified at the wrong moment. Three unanswered batches and it stops for
the rest of the session, giving up the batching and never the channels: a
request that failed says nothing about which sensors the car has, and until then
each failed batch is retried one parameter at a time so nothing blinks.

Reading the reply is where the care went. It is multi-frame, so an adapter
prints it as a length header and segment-marked lines — and `0:` and `1:` are
themselves valid hex, so anything that strips non-hex characters and pairs the
rest shifts every byte after the first marker by half a byte. There is nothing
between the groups either, so the walk stays in step only while every parameter
number is recognised, and one that is not stops it rather than guessing a
length. And more than one module answers: a reply can be two responses run
together, so **every possible starting point is scored by how much of the reply
it explains** and the best one wins. Taking the first plausible one is not a
theoretical problem — on a live car a leading fragment came first, yielded one
parameter of four, and read as "this car cannot batch" on a car that could.

**A parameter that has answered is never given up on.** Both paths share this
now, and it was wrong before: six consecutive silences retired a parameter
whatever its history, and the retirement is never undone — so one bad moment
cost a live gauge for the rest of the drive. Only parameters that have *never*
answered are dropped from the rotation.

**And a reply carrying only some of what was asked for is not a round that
worked.** Whatever it left out is asked for singly, exactly as it would have
been with no batching at all. Without that it was asked once and then never
again — nothing comes back to a parameter that has answered before, so one that
stopped appearing in the batched reply simply kept its gauge, showing a reading
from minutes ago and looking live the whole time. A segmented frame that lost
its tail, or a second module's line that missed the window, is all it takes.

**Some dongles cannot survive being asked that way, and the verdict is now
remembered.** A Vgate iCar Pro Wi-Fi does not refuse a batched request. It
answers one — completely, on time, on a healthy link — and then the TCP session
is gone: every read afterwards returns nothing while the socket still reports
itself connected. The batch that killed the link therefore looks like a success,
and the only thing identifying it is the silence that follows — which, by
itself, is also precisely what a key turned off looks like. Three unanswered
batches will not catch this one: singles fall silent along with the batch, and
that is the shape of a link that has gone rather than a request that was refused.

So it is caught on the way back instead. **A reconnection does not probe** — the
probe is a batched request, and one sent to rebuild a link that batching has just
killed kills the replacement as fast, every attempt, for as long as the
reconnection window lasts. A link that died with batching on comes back without
it, and single requests answering across that reconnection are the evidence: the
car is there, the adapter is there, and the one thing that has changed is the
request that was in flight when it died.

Learning that costs a dropped link and several seconds of blank gauges, so it is
learnt once rather than once per drive: the verdict goes into the settings file
against that adapter, and one with form is not probed again. That last part
matters more than it sounds — the probe is itself a batched request, so probing
a dongle already known to fail is not a cheap check, it is the entire cost of
the thing being checked for. Two bad links before it is believed, because a link
also dies from going out of range or the key turning off, and condemning a
capable adapter on one of those would quietly cost the advantage for ever. A
different dongle starts clean.

**A reply whose length is knowable no longer waits for its prompt.** On the same
adapter the `>` trails the payload by about 200 ms, and with batching dead that
gap *is* the poll cycle. A mode 01 request has a defined answer length, so the
answer now ends itself the moment it is complete. Three guards, each of which
turns every way of being wrong into an ordinary wait rather than a short read:
it must begin `41`; it must be nothing but hex; and anything whose length
depends on the answer never comes here at all. The first is subtler than it
looks — `NO DATA` is caught by having letters in it, but a negative response
like `7F 01 12` is pure hex and exactly as long as a one-byte answer, and on a
broadcast that refusal can be one module's while the real answer is another's,
arriving behind it.

**A link that answers nothing to a reset is a corpse, not a slow adapter.** `ATZ`
never reaches the vehicle — an adapter answers it out of its own firmware — so
silence there is a session that has died. Recovery used to walk on into the
warm-up, the protocol question and a whole poll round before anything concluded
what the first reply already said, which is fifteen to twenty seconds of blank
gauges per attempt; it now hangs up after a second opinion and lets the
reconnect open a fresh socket. A reset that hears nothing at all also stops
configuring the five options on a link that is not there, at a timeout apiece.

**Recording is yours to start and stop.** A **Record** button appears next to
*Connect* whenever a session is live, and there is a matching pair in *Tools*.
Recording and watching used to be the same act — a session wrote from connect to
disconnect under a name nobody chose — which is the wrong unit of work. The
interesting part of a session is a pull, or a lap, or the two minutes after a
change, and none of those begin when the cable goes in.

You choose the moment, the name and the folder. The next recording is offered
the folder the last one went to, and the suggested name carries the ECU and the
time, so a session can produce several files without any of them being called
"log (final)". Each one is a log in its own right: its clock starts where the
recording started, not where the session did, so it opens without seven minutes
of nothing in front of it.

**Connecting no longer records on its own.** A session is opened to check a
link, read a gauge or watch a change far more often than to capture anything, and
every one of those used to leave a file behind — so the recordings folder filled
with runs nobody wanted and the one that mattered had to be found among them.
*Tools ▸ Record as soon as I connect* puts the old behaviour back and is
remembered.

This changes what an existing install does, deliberately. What it costs is a run
somebody meant to capture and did not, so the state is stated wherever a session
is rather than left to be inferred from silence: the toolbar button, the status
bar — "REC 1,204 rows" or "not recording" — and the hint on connecting all say
which of the two is happening.

**A steady sensor is drawn as steady.** Every trace is scaled to its own range,
which is what lets a dozen channels with different units share a plot — and it had
one failure that was the first thing a new log showed you. A manifold pressure
holding 12.0 within a tenth had its last decimal stretched to the full height of
the lane and came out as a wall of noise, indistinguishable at a glance from a
channel swinging idle to redline.

A trace now gets a floor on its drawn range, proportional to the channel's own
magnitude — proportional because there is no unit available, and a tenth is
nothing on a manifold pressure and everything on a lambda reading. The floor
widens a range and never replaces it, so the effect tapers: a sensor jittering one
per cent of itself ends up occupying a quarter of its lane, while a lambda
wandering four per cent still fills four fifths of one. There is no point at which
a trace visibly snaps between two treatments.

*View ▸ Draw steady channels as steady* turns it off, because the shape it hides
is exactly the shape somebody chasing a slow drift is looking for.

**Compare two logs.** *File ▸ Compare against another log…*. The loop tuning
actually is — change something, drive it again, find out what moved — and until
now the application could only hold one log at a time.

The table becomes a **difference**: the same cells of the same table, one run
subtracted from the other, so "at 3,000 rpm and 150 kPa it is 0.4 richer than it
was" is a statement about the change. Nothing is interpolated onto a shared
timebase, deliberately: comparing two runs at the same clock time assumes they
were the same run, which is the thing being tested. Binning both onto one grid
does not care that one drive was longer or started in a different gear.

Three things it refuses to do quietly. Channels are matched **by name**, never by
column position — a firmware update that inserts one channel shifts everything
after it, and matching by index would compare coolant against oil pressure
without a word. A cell only one run visited is left **empty** rather than treated
as zero, which would otherwise invent a difference the size of the whole reading
everywhere the second drive did not go, and those would be the biggest numbers on
the table. And each cell carries the **smaller** of the two sample counts, because
a difference is only as well evidenced as its thinner side.

The second log is binned onto the first one's axes rather than its own, which is
the part that would have been wrong invisibly: two logs binned independently pick
their own ranges from their own data, so their cells would not line up and the
subtraction would compare 2,400 rpm against 2,650 while looking entirely
reasonable.

**Runners, plenum and headers.** *Calculators ▸ Engine ▸ Runners & headers*.
Intake runner length and bore, plenum volume, exhaust primary length and bore,
collector size, and the gas volume of every one of them — for a naturally
aspirated or a turbocharged engine, across an rpm range you give it.

A manifold is not a plumbing problem. An opening valve launches a pressure wave
along the pipe, the far end reflects it back inverted, and if it arrives before
the valve shuts it either packs charge in or pulls exhaust out. Length decides
when it arrives, so length decides the engine speed it works at — which makes it
a choice about what the engine is for, not a correct answer waiting to be found.

So the page asks where you want torque and where you want power, and offers three
ways to spend the difference. **Quick spool** tunes at the torque peak with small
fast ports and a small plenum. **High-rpm race** tunes at the power peak with big
ports and a large one, and is allowed to be soft at 3,000 rpm because it is never
there. **Street and strip** sits between them. On a turbocharged engine the plenum
comes down again, because a plenum is dead volume that has to be pressurised
before boost is anywhere.

**Five cams to pick from, so the page works without a cam card.** *Stock*,
*Stock +*, *Performance*, *Performance +* and *Full race*, each filling in both
durations and the gas temperatures that go with that sort of build. Duration
drives every length here and it is the number somebody planning a build is least
likely to have to hand; asking for it as a bare figure in degrees gets either the
default left alone or a number invented, and both produce lengths that look
authoritative and are wrong.

The ladder is described by what the engine is like to own rather than by peak
power, because that is what actually decides whether somebody can live with a
cam. Each step adds overlap — 33°, 46°, 60°, 78°, 102° — and overlap is where
idle quality, manifold vacuum and the whole possibility of scavenging come from.
The list says so in those terms: *Stock* keeps full vacuum for the brakes,
*Performance +* wants a high-stall converter, *Full race* has no idle worth the
name.

The figures track what reputable grinders sell and are checked for internal
consistency rather than taken on trust. The overlap each entry implies is worked
out from its own durations and lobe separation — (DI + DE)/2 − 2·LSA — and lands
where catalogue grinds of that description land; the formula reproduces Comp's
252H at 32° and their XE274H at 60° exactly.

**And it carries both duration figures, because that is the trap.** A cam card
leads with duration at 0.050 in lift, since that is what grinds are compared on,
and it runs 44 to 52 crank degrees shorter than the seat-to-seat number. The wave
does not care where 0.050 in is; it cares when the valve opened. Putting a 0.050
in figure into a box that wants seat to seat takes about a fifth off every runner
and primary on the page, and nothing about the answer looks wrong — there is a
test holding that claim to between 12 and 25 per cent. So each entry shows both,
the 0.050 in pair is printed alongside for checking against a card, and the boxes
say which one they want.

Picking a cam fills the boxes; typing in a box moves the list to *from your cam
card* rather than leaving a description that no longer applies — the same
arrangement the volumetric efficiency list uses elsewhere.

**A turbocharged spool build now gets the short runner, not the long one.** This
was backwards. The recommendation had always been the longest pipe that packages,
because on an atmospheric engine the returning pulse is the only help there is.
Under boost the trade reverses: the resonance is worth a few per cent of filling
against fifty or more from the compressor, while the pipe's volume is charge that
has to be pressurised before any boost arrives at all. Same goal, opposite answer,
and the induction is what decides it. On a 2.0 litre wanting boost by 3,500 rpm
that moves the runner from 561 mm to 370 mm.

**Plenum volume is a dial rather than three presets.** A table of every quarter
step from half displacement to twice it, with the volume each comes to, the
runners added on, and what each one does — worded differently for a turbocharged
engine, because there the same volume also has to be filled before boost. The
figure actually in force gets its own marked row even when it is not one of the
steps.

The turbocharged spool default came down from 0.90 to 0.75 of displacement.

**And the page now says how much of spool this is really worth.** *Plenum and
runners* is the charge the compressor has to pressurise that this page can see —
and the note beside it says plainly that it is not most of it. On an ordinary
front-mount installation the intercooler core and its two pipe runs come to around
three quarters of the tract on their own: roughly 10 litres against 1.5 in the
plenum and 1.1 in the runners. Taking a plenum from 0.90 to 0.75 moves about two
per cent of the total, and the shorter runner about five. Both are real levers and
both are small, and anyone chasing spool through plenum volume alone is working on
the wrong quarter of the problem.

**Volumetric efficiency now follows the head and the cam together.** The page
gained an *Engine type* list — the same one the recipe and turbo pages use — and
the figure in the box is worked out from it and the chosen cam rather than left
at a flat 100. It fed straight into port sizing, so a stock cam used to get ports
sized for an engine breathing like a race motor.

Neither input answers it alone. The head says what the ports flow, the cam says
how long they are held open, and — this is the part that was buried in prose
until now — the figure quoted for a head already assumes a particular cam. Every
entry in the list now names the cam it was quoted with, so choosing one counts
the difference from that assumption instead of counting the cam twice. The race
entry is the only one whose description already has a big cam in it, so it is the
one that *loses* ground as the cam comes back: 105 with the grind it assumes, 89
on a stock one.

A cam cannot carry a head past what it flows, so the gain is capped. An old
two-valve given a full race grind reaches 88 and does not turn into a four-valve;
a modern pushrod V8 goes 85 to 98 across the ladder. Four points a step, capped
at thirteen — conventions, quoted with the same warning the volumetric efficiency
list has always carried, that two engines answering the same description differ
by more than this on port work alone.

Typing your own figure still wins and moves the list to *measured or known*,
because a number off a dyno beats anything worked out from two descriptions.

One thing this turned up: two heads can land on the same figure — a four-valve on
fixed cams and a race intake both reach 105 with a full race grind, by different
routes. The list would have jumped from the head you picked to whichever was
listed first, which looks like the page overruling you. The selection already
showing now wins any tie.

**Choosing turbocharged now brings its pressures with it.** It used to leave the
manifold and the exhaust both at atmospheric, which describes an engine that
cannot exist, and the page would immediately say so — a poor first thing to meet.
It now opens at about 14 psi of boost with the exhaust manifold 20 per cent above
it, because a turbine has to be driven and always sits higher than the compressor
side. Both stay editable, and switching back to naturally aspirated puts them
back.

**Every harmonic is shown, not just the chosen one.** A given engine speed has
several lengths that work — the wave can make two round trips, or three, or six —
and a lower order is a longer pipe with a stronger pulse. The table gives all of
them and marks which package in a car, because tuning a turbo engine at 3,500 rpm
wants a metre of runner at the second order and nobody has a metre. The
recommendation is the longest one that fits; the rest are there for when it does
not.

**The arithmetic is derived, not fitted, and that means it can be checked.**
Lengths come from the speed of sound in the gas and the time the valve is open;
bores come from mean velocity through the valve-open window. Against A. Graham
Bell's published empirical primary length — a fit to engines that were built and
dynoed, sharing no constant with any of this — the derived answer agrees within
about five per cent across cams from 250° to 300° and speeds from 5,500 to 8,000
rpm. Read the other way, Bell's constant implies a mean primary gas temperature of
536 to 676 °C, and the page defaults to 600. Both directions are tests.

Exhaust bore is calibrated the same way. Published figures for "exhaust gas
velocity" vary threefold between sources because they are averaged over different
things, so rather than import one whose basis cannot be checked, the target is
worked backwards out of headers that are on engines — a 1.6 at 8,000 rpm on 1.5 in
primaries, a 2.0 on 1.625, a 5.7 V8 on 1.75, a 6.2 on 1.875. On this page's basis
all of them fall between 168 and 211 m/s, and asked to size those four engines the
calculator lands within a quarter inch of what they actually run. It also names
the nearest tube in eighths, because that is how header tube is sold.

**On a turbocharged engine it says the header length is not the lever.** A turbine
reflects no usable wave back down the primary, so the length is shown for interest
and the total manifold volume is given instead — that is the number that decides
how fast it spools. Boost is carried through properly: denser exhaust gas needs
*less* pipe area, not more, and the page warns when back pressure has been left at
atmospheric, which no turbine produces.

Lengths are from the valve head to the open end, so the port in the cylinder head
is part of the runner and part of the primary — forgetting it is the commonest way
one of these comes out long. The figure given is what a tape measure should read,
the acoustic length it came from being a few millimetres more because a pipe
behaves longer than it is.

What it is not is a flow bench. It sizes pipework from wave timing and mean
velocity and knows nothing about port shape, valve curtain area, the short-side
radius or a badly cast bend, and no length here will rescue a port that does not
flow. It gets a design to the right size before any of that is worth measuring,
and it says so on the page.

**Intercooling, and chemical intercooling.** *Calculators ▸ Air & boost ▸
Intercooling*. What the compressor does to the air, how much heat there is to
take back out, what a core will actually remove, and what spraying something into
the charge does instead.

The air side is textbook and worth having in one place: pressure ratio, the
compressor outlet temperature at a stated efficiency, the heat load in BTU/min
and kW, the outlet at a given effectiveness, and the density — which is the number
that turns degrees into torque. Core dimensions give frontal area, volume and a
loading figure. That last one is deliberately a comparison rather than a verdict:
there is no honest way to predict a core's effectiveness from its outside
dimensions, so the figure exists to set two candidates against each other, and
the page says so rather than pretending otherwise.

**On material, the calculator argues against the question.** A half-millimetre
aluminium wall is under one per cent of the resistance between the two air
streams — the air film is what holds things up, not the metal. Copper is nearly
twice the conductivity and over three times the weight, and buys essentially
nothing. The page shows that share rather than offering a fake performance
multiplier per material.

**Chemical intercooling** covers 100% water, 70/30, 50/50, 30/70, 100% methanol
and E85, in cc/min for buying a nozzle and lb/min for the physics. Latent heats
are the published thermophysical figures — water 2,260 kJ/kg, methanol 1,100,
ethanol 850 — converted once and tested against the source values.

Three things it gets right that are easy to get wrong. **A 50/50 mix is sold by
volume and behaves by mass**: methanol is lighter, so half a litre of it is 44% of
the weight, and using the volume fraction understates the cooling. **Not all of it
evaporates where it is wanted** — what wets the pipe still works against knock but
does nothing for density, so the evaporated share is a control rather than an
assumption. And **methanol is fuel**: a pound carries 8,640 BTU against petrol's
18,400, so the page reports the petrol it displaces, because an engine given both
runs rich with the spray and leans out hard the moment it stops.

It also says plainly what a nozzle calculation cannot: an engine tuned to lean on
a spray finds its detonation limit within seconds of a failed pump, and sizing the
nozzle is the easy half of that job.

**Fuel economy: three vehicles side by side.** *Calculators ▸ Running costs ▸
Fuel economy*. Petrol, hybrid, E85, diesel, CNG or electric in each column, with
what each costs by the week, the month and the year, per mile, and how many
gallons or kilowatt-hours it gets through.

Two things it does that a spreadsheet usually does not. **A gallon is a volume,
not an amount of energy** — E85 holds about three-quarters of what petrol does
and diesel about an eighth more, so the same miles per gallon on two fuels is not
the same efficiency. Every column therefore also shows MPGe, which is the only
figure on the page that compares three fuels honestly, and entering an E85
economy copied across from the petrol column is called out rather than quietly
costed. **And an electric car is billed at the meter, not at the battery**: about
a tenth of what you pay for never reaches it, which is included in the cost and
excluded from the efficiency, since charging should not be charged for twice.

Starting prices are US national averages captured 4 August 2026 — petrol, diesel
and E85 from AAA, electricity the residential average, CNG from the Alternative
Fuels Data Center, whose national figure is the oldest of the set and says so.
They are a starting point, not an answer: prices move weekly and differ by
dollars between states, so every one is editable and the page says where each
came from and when. It counts fuel only — not tyres, servicing, insurance or
depreciation, any of which can dwarf the difference shown.

**Subaru's own protocol, over an ordinary dongle.** *Connect ▾ → Connect over SSM
(Subaru)*. SSM reaches what the ECU has **learnt** rather than what it is
measuring — knock correction, learnt ignition timing, the fuelling trims — none
of which OBD2 carries at any speed.

The received wisdom is that ELM327-compatible adapters cannot speak SSM, and over
the older K-line cars that is true. Over CAN it is not: a 2014 Crosstrek answered
an OBDLink r2.6 directly, confirmed by reading values over SSM that could also be
read over OBD2 — engine speed landed between two OBD2 readings taken either side
of it, and coolant returned the identical raw byte to PID 05.

One address per request, about 146 ms each, so a dozen parameters is roughly a
round a second. That is slow and it suits the job: these are learnt values that
move over minutes. It is useless for catching a misfire and the hint says so. The
reason it cannot be faster is written down rather than assumed — the ECU refuses
the block read, a two-address request needs eight bytes where the adapter caps at
seven, and the extended send command that exists to solve exactly this is absent
from that firmware.

**The addresses are yours to supply**, in `ssm-parameters.json` in the definitions
folder, which is written with a worked example the first time it is wanted. They
are not shipped, and that is deliberate: what lives at which address cannot be
worked out from the car alone — the confirmation method only covers values OBD2
already has, which are the ones not worth reaching SSM for — and the published
maps belong to projects under licences an MIT release cannot take from. Supply
them from a source you judge right for your own vehicle. It also means this works
on any Subaru rather than one.

**Getting back to the ECU you were on.** The toolbar carries a **Connect: _your
ECU_** button for the last device you used, **Ctrl+K** repeats it, and the connect
menu now groups devices that have answered before above the rest, most recently
used first. Devices are keyed by hardware id rather than COM port, so a replug
that lands on a different number is still recognised, and the button names the
ECU rather than the port because the port number is Windows' business.

Never automatic. Launching the application does not take a serial port on its own
— that would fight TunerStudio for it, and would start a session that somebody
opening a saved log never asked for.

*Tools ▸ Forget remembered ECUs* clears the device list and nothing else. Presets,
filters and calculated channels are your work and are not swept up in it.

None of your profile ships with the software: presets, filters, calculated
channels and remembered devices all live in `%APPDATA%\OpenLogViewer` and a new
install starts empty. The filters offered on opening a log are generated from
that log's own channels and arrive switched off.

**Fault codes, read and cleared.** *Tools ▸ Fault codes…* on an OBD2 connection.
All three of the standard's lists, because they mean different things: confirmed
codes are what lit the lamp, pending ones were seen once and are not yet believed,
and permanent ones cannot be erased by anybody but the controller. Each code
carries the standard's own definition where it has one.

Where a code belongs to the vehicle manufacturer it gets no description, and says
so. Those ranges are the maker's to assign, so the same five characters mean
unrelated things on two cars in the same street — a plausible guess is how
somebody buys a part they did not need.

Erasing is behind a confirmation that says what it actually costs. Mode 04 does
not clear the fault, it clears the evidence: the freeze frame — the only record
of what the engine was doing at the moment the fault occurred — goes with the
code and cannot be recovered, and so do the readiness monitors, which the car has
to re-earn over a full drive cycle before it can pass an emissions test. Anything
permanent is read back afterwards and reported, because it survives.

**Calibration becomes the diagnostics tab on an OBD2 car.** A standard vehicle
has no tune and never will, so that tab used to tell somebody who was plainly
connected to "connect to an ECU" — describing a state the application was not in.
It now shows the same fault list, full width, and scans the first time you switch
to it. *Tools ▸ Fault codes…* still opens it in a window; both are the same panel,
because two copies of a diagnostic view drift and only one of them gets the fix.

A scan can be run with the session polling. The adapter takes one command at a
time and the gauges stop for a second or two while it does, which is honest about
what OBD2 is.

**VE Calibration.** Suggests a new fuel table from logged AFR against the AFR the
tune was asking for. In histogram view, pick one of the tune's own tables under
*Axis breakpoints*, set *Compare against* to the AFR target, and tick **Suggest
a new fuel table**.

It refuses to guess. A cell under the sample threshold is left alone and
reported as thin; a correction beyond the per-pass limit is clamped rather than
applied whole; a cell the log never visited stays untouched rather than zeroed;
a zero or negative target is not treated as a target. Toggle between how far
each cell moves and the new numbers themselves, and export either as CSV in the
shape a tuning table has.

Verified against a 60,356-sample MS3 log and its tune — 128 of 256 cells
suggested, 62 too thin — with the same result whether the tune is read from
inside the log or from the `.msq` alongside it.

**Opening a tune.** Tables come from the log by default. *Open tune…* sits
beside *Open log…* in the toolbar and again next to *Axis breakpoints*, and a
`.msq` can be dropped on the window — needed for `.msl` and `.csv` logs, which
carry no tune. The toolbar always names the tune in use, so which one is loaded
is visible from the main view rather than only from the histogram sidebar.

For an MLG it is usually the wrong thing to do, so the sidebar compares the two
and warns when the opened tune's fuel table differs from the log's: VE Calibration
scales the numbers that produced the logged AFR, and a table edited since the
drive scales numbers the engine never ran.

**Four more calculators, and one that ties them together.** *Engine recipe* takes
a displacement, a power target and the two engine speeds and hands back a parts
list: the air it takes, the boost that needs, a turbocharger, injectors and a
pump. Every part of it is sized on one mixture and one fuel consumption, so the
pieces match each other — a turbo sized at one assumption and injectors at
another gives a car short of one of them and neither calculation would say
which. The margins stay separate and named: duty on the injectors, headroom on
the pump, flow to spare on the compressor.

The peak torque speed is an input because it decides as much as the power figure
does. Airflow at the power peak says whether a compressor is large enough;
airflow at the torque peak says whether it is too large — one with plenty left at
7,000 rpm can be sitting off the left of its map at 3,500 and never spool.

*Turbo sizing* is the same sum on its own, using the turbocharger maker's own
equations so it can be checked against the tool everyone already uses: their
published worked example comes out at their 57.3 lb/min and their pressure ratio
of 2.0. The catalogue carries inducer size and the maker's horsepower rating —
which on the G series is the model number — and derives flow from that rather
than transcribing it off a compressor map, since the same map gives a different
maximum depending which island you read it at.

*Drag strip* gives quarter and eighth mile from power and weight, and reads a
timeslip the other way. Trap speed and elapsed time are not equally trustworthy:
by the far end a car has had a quarter of a mile to forget a bad launch, so its
speed is very nearly a measure of power against weight, where the time never
forgets. So the trap is read for power and the time for the start line — a slip
that trapped like 400 hp and ran half a second slower than that trap deserved
lost the time in the first sixty feet, and nothing done to the engine will show
up until it is found.

Volumetric efficiency is chosen as a kind of engine rather than typed as a
number, since that is what somebody planning a build actually knows: an older
two-valve breathes 75 per cent where a race engine on tuned inlet lengths passes
100. Charge temperature takes Celsius or Fahrenheit, fuel selection moves the
mixture and the fuel consumption together — moving one without the other asks
for half again the air on E85 and buys two sizes too much turbocharger — and the
calculators are grouped down the side rather than tabbed, with the whole set one
click away on the toolbar.

**Editing a tune table by hand.** Nudge, scale, set and interpolate from buttons
rather than only from keys, and a readout above the table saying what the
selected cell would become — *2600 rpm × 100  98 → 100  +2 % (2.0% more)* —
before any of it is sent. Interpolation fills the middle of a selection from its
ends, in a row, a column or a rectangle, and leaves the ends exactly as they
were.

**Sending a tune to the ECU.** *Send* writes the changed cells to a connected
controller and *Burn* commits the page to flash. They are two buttons rather than
one because they are two different risks: a write is gone at the next power cycle
and a burn is not.

Both confirm first, and the confirmation says the thing worth knowing rather than
asking whether you are sure. Send names how many cells are about to change —
a table scaled by five per cent when one cell was meant is 256 changes, and it
looks identical to one change until it is counted — and says the write reaches a
running engine immediately. Burn says it is permanent, and to do it with the
engine stopped, because the controller stops answering while it writes flash.

A write is read back from the ECU and compared before it is called a success. An
acknowledgement only says the command was understood, not that the right bytes
landed at the right offset, and every way of getting that wrong ends with an
engine running on numbers nobody chose. A mismatch is reported as a failure that
a power cycle undoes, because nothing has been burned.

Verified against a live MicroSquirt: one cell changed here, confirmed changed in
TunerStudio — which is the check a whole-table write would hide, since it catches
a wrong page, a wrong offset, wrong scaling and a transposed table all at once.

**Estimated horsepower, two ways.** *Tools ▸ Estimate power…* adds calculated
channels that work out what the engine was making, from a log a logger already
recorded.

Speed density takes the air from the manifold — pressure, temperature, engine
speed and how completely each stroke fills — and turns it into power through the
mixture and the fuel consumption. The injector route ignores the air entirely and
counts the fuel instead, from how long the injectors were held open and how hard
the rail was pushing on them. Dead time comes off the pulse width first, batch
injection is worth exactly twice sequential, and flow follows the square root of
the pressure *across* the injector — so a rail measured against the atmosphere
while the injector sprays into a boosted manifold flows less than its rating, by
the boost. Where the car has a mass air flow sensor, that is a third route.

The two rest on almost nothing in common: one leans on volumetric efficiency, the
other on injector data. So a **spread** channel reports how far apart they are,
and that is the output worth watching. A few per cent is noise. A steady gap
means one of the two is wrong — and the assumed fuel consumption cancels out of
the comparison, so the spread is telling you about the engine rather than about
the guess. On a test log with the injectors described correctly the two agree to
0.0%; describe the injectors as 550 cc when they are 1,000 and it reads −50%;
claim 120% volumetric efficiency on an engine making 95 and it reads −21%.

Everything the log can answer, it is asked: the mixture, the volumetric
efficiency, the rail pressure and the manifold are read from the log wherever
they are there, whatever the firmware called them and whatever units it chose —
kPa or psi, °C or °F, lambda or an air-fuel ratio. The dialog only collects what
a log cannot say. Methods the log cannot feed are not offered, and it says which
sensor each one wanted.

None of it is a dyno, and it says so. Every method multiplies by a brake specific
fuel consumption that has been assumed rather than measured, so the absolute
figure carries that error. What they are good for is shape and change — where the
power is made, what a modification did, whether two runs match — and those are
ratios, which the assumption divides out of.

**Calculated channels.** Define a channel from the ones the log already has:
`AFR - AFR Target 1`, `RPM * Torque / 5252`, `if(Boost psi > 0, Boost psi, 0)`.
Once built they are ordinary channels — plottable, usable as a histogram axis,
available to filters, included in exports — and marked **ƒ** in the list.

Names need no quoting even with spaces in them. Operators `+ - * / % ^`,
comparisons, `&& || !`, and `abs sqrt min max clamp floor ceil round log log10
exp pow sign if`. Missing readings propagate, including through comparisons.
Stored in `math.json` by name and expression, so a set written for one car
applies to any log carrying those channels.

**Calculators.** *Tools ▸ Calculators*: fifteen of them by the end of this
release, grouped down the side into planning a build, air and boost, fuel,
engine, drivetrain and running costs, and all recomputing as you type. A list
rather than a row of tabs because tabs run out of width at about eight — which
was the right call, since this started at nine.

Engine takes a bore, a stroke and a cylinder count and answers three questions at
once, because all three rest on the same two numbers: how big the engine is, how
fast the pistons are going, and how hard it squeezes. Mean piston speed says more
about what an engine is being asked to survive than its rpm does — seven thousand
is an easy afternoon for a short stroke and the end of the road for a long one.
Compression is the static ratio from the chamber, gasket, deck and crown, with
the sign conventions spelled out, and alongside it an index of what your boost
does to it: less compression buys boost, and it says roughly how much.

Boost converts psi, bar and kPa, and gauge against absolute — an ECU reports
manifold pressure absolutely, so ten psi of boost is 170 kPa and not 69.

Pressure ratio is the compressor's own, taken at the compressor rather than at
the manifold: the air filter costs something on the way in and the intercooler
costs something on the way out, and both push the ratio up. Altitude is an input
and defaults to sea level. It matters more than people expect — a gauge reads
against whatever the engine is breathing, so the same twelve psi is a ratio of
2.10 at sea level and 2.34 at five thousand feet, which can be the difference
between a compressor sitting in the middle of its map and one against the edge
of it. Set the altitude or type your ECU's own barometric reading, and every
other tab follows it.

Injectors size from power, cylinders, BSFC and duty, with the cc conversion
taken from the fuel's own density. Each fuel is advised with the BSFC it
actually wants, worked out from its energy content: E85 needs about half again
petrol's and methanol a little over twice, and leaving it at petrol's is what
undersizes an injector.

The fuel pump gives what is burned and what to look for with headroom over it,
and then names pumps that would do it — Walbro and AEM part numbers, with how
many of each. Compared at the pressure the pump will actually see rather than the
one its headline figure was measured at, because a rail at 43 psi with 20 psi of
boost on it is 63, and every pump makes appreciably less there than the number on
its box. Alcohol fuels are only ever offered pumps rated for them. Past three in
parallel it says so and points at a mechanical or brushless pump instead of a
fourth. The BSFC follows the fuel, scaled by energy content so a figure you
measured or chose for an aspirated engine survives the change, and a legend
alongside shows the typical figure for every fuel.

Gearing gives road speed against engine speed in every gear: what each is worth
at the redline in mph and km/h, how long-legged it is per thousand rpm, where the
engine lands on taking the next one, and what it turns at a cruise. Tyres are
entered as written on the sidewall. The squat of a loaded tyre is an input rather
than a silent error — a rolling tyre covers about three per cent less ground than
pi times its diameter, so working from the geometry alone reads that much fast,
which is roughly what a factory speedometer already does and why the two are easy
to confuse. Top speed is named as the geared one: the tallest gear at the redline
and nothing else, which is a ceiling rather than a speed the car will see.

Octane estimates what blending ethanol, methanol or E85 into pump petrol is
worth, as a chart of the six pump grades against blend fraction, and shows how
much colder the blend runs the charge. The first splash of ethanol being worth
far more octane than the last is not the mystery it looks like: octane blends
very nearly linearly by molecule, and ethanol's molar mass is 46 against petrol's
105, so a tenth of the volume is a fifth of the molecules. Estimated on that
basis rather than by volume, and checked against measured blends — on an 88 RON
blendstock it gives 92.4 at E10 and 98.7 at E30 where the measurements are 92.4
and 98.6.

Lambda converts either way on ten fuels, with the blends computed from their
constituents by mass. Airflow gives demand in cfm, in the pounds per minute a
compressor map is read in, and in cubic metres an hour — and what that air is
worth in power on petrol, ethanol and methanol. The answer there is worth
knowing: it is very nearly the same on all three, because a pound of air carries
about as much energy whichever fuel arrives with it. What makes the alcohols
worth having is knock resistance and charge cooling buying boost and timing,
which arrives as more air rather than as more power per pound of it.

Every figure is computed in the core and tested there against numbers a tuner
would recognise — a turbocharger maker's own worked example, the published
standard-atmosphere table, familiar injector sizings — rather than against the
formulas restated. The advice the window gives is tested alongside them, because
a tuner acts on a sentence as directly as on a number.

**Export.** *Export ▾* in the toolbar. In log view: the plotted channels or all
of them as CSV, and the plot as a PNG. In histogram view: the binned values, the
per-cell sample counts, and the table as a PNG. Mark a span first and the CSV
covers only that span.

Numbers are invariant-culture and each value is the shortest text that reads
back as the same sample. An exported log opens again in OpenLogViewer, gaps in
logging included. `--export <folder>` does the same without dialogs.

**Fourteen colour schemes.** Two dark, two light, eight after well-known editor
schemes, and a high-contrast pair. Chosen from the toolbar and remembered.
Chrome, plot, heat table and scrollbars all follow the scheme.

Each scheme carries its own trace palette, and switching re-picks the colours of
everything plotted. Those palettes are not the upstream editor colours: a syntax
theme colours short spans that are never adjacent, and every canonical palette
failed when measured for two traces crossing mid-plot. Each keeps its scheme's
hues and relative saturation, moves only in lightness, and was checked for
lightness range, chroma, contrast against its own background, and neighbour
separation under protanopia and deuteranopia.

`--theme <id>` starts in a scheme for one run without changing the saved
preference.

### Changed

**Logs use about half the memory.** Channel samples are stored as 32-bit floats.
No logger produces more precision than that — the widest MLG field is an f32 and
text logs come from short decimal strings. A 179-channel, 12.4 MB log now
retains 26.6 MB instead of 52 MB.

The time base is the exception and keeps full precision, because it accumulates:
ten hours into a 413 Hz recording the float step exceeds the interval between
samples, and time would stop advancing.

**Logs load with about half the allocation.** Both readers decode straight into
float columns instead of building the whole log as doubles first. The delimited
reader also stopped growing a list per column and copying it out. For the
reference log, 90.3 MB allocated became 39.6 MB and peak working set 113 MB
became 61 MB.

### Fixed

- **MLG scales carried float error into every sample.** Descriptors store the
  scale as a 32-bit float, so a channel scaled by 0.1 held 0.100000001490116.
  A raw 341 decoded to 34.10000228 instead of 34.1 — one rounding step off,
  hidden behind the display format but plainly wrong in an exported file.
- **The sidebar listed "Time" as a plottable channel.** The time base is built
  from its own copy of the column, so it is never the same object as the entry
  in the channel list, and both places that excluded it compared references —
  and therefore excluded nothing.
- **PNG exports were shifted and clipped.** `RenderTargetBitmap.Render` draws a
  visual at its position within its parent, so the plot came out offset by the
  width of the sidebar with its right edge cut off.
- **Scripted runs could hang silently.** `Dispatcher.InvokeAsync` captures an
  exception into the operation it returns rather than raising it, and nothing
  awaited that operation, so a failed `--screenshot` or `--export` left the app
  open with no error and no exit. Both now report and exit non-zero.
- **Both diverging heat-table arms faded towards the same near-white**, so the
  worst rich cell and the worst lean cell looked alike.
- **An accent close in lightness to both the background and the text** left a
  hovered button's glyph unreadable.
- **A blue heat-table ramp starting too dark** sat level with an empty cell,
  making "never visited" look like "visited once".
- **App tests read and wrote the user's real settings.** The harness built a
  temporary directory and then constructed the view model with default stores.

### Internal

- Trace colours come from the view model's own theme rather than global state,
  which parallel test classes could race on.
- `MathChannel` deliberately avoids `required` members: a required member
  missing from the JSON fails the whole document, so one hand-edited entry with
  a typo would take every other definition with it. `LogFilter` has the same
  shape and the same latent behaviour, left as shipped.
- Heat-table ramps are derived in OKLab from each theme's anchors and pinned by
  tests, rather than listed per theme.

### Still open

- Loading blocks the UI thread. A 12.4 MB log freezes the window for about
  200 ms; a much larger SD-card log would be seconds.
- `MainViewModel` has grown past 1,000 lines and holds theme, filters,
  histogram, presets, calculated channels and the channel list.
- Text logs allocate about 59 MB parsing a 3 MB `.msl`, nearly all of it
  per-cell substrings. Retained memory is unaffected; this is load-time churn.
