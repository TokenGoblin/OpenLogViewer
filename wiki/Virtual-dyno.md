# Virtual dyno

Works out what the engine made from a full-throttle pull already in your log, by
three independent routes, and draws them over one another.

- [What it does](#what-it-does)
- [The three routes](#the-three-routes)
- [Requirements](#requirements)
- [How to run it](#how-to-run-it)
- [What you have to type](#what-you-have-to-type)
- [What the recording knows](#what-the-recording-knows)
- [Choosing the gear](#choosing-the-gear)
- [Reading the sheet](#reading-the-sheet)
- [Wheels or crank](#wheels-or-crank)
- [Air and corrections](#air-and-corrections)
- [Why a pull was not offered](#why-a-pull-was-not-offered)
- [Settings](#settings)
- [What it will not do](#what-it-will-not-do)
- [Troubleshooting](#troubleshooting)
- [Related](#related)

---

## What it does

A dyno holds an engine against a known load and measures what it takes to move
it. Your car did the same thing on a road: it accelerated a known mass, and the
log recorded how fast. Power is what that took.

> The car weighs a known amount. It went from this speed to that speed in this
> long. Accelerating a mass takes power, and pushing it through the air takes
> more.

That is the whole of the first route. Nothing about it assumes anything about
combustion — it does not care what fuel you burn or how well the engine breathes,
only what the car did. The other two routes ask the engine instead, and the point
of having three is that they can be checked against each other.

The result is an **estimate**, not a dyno figure. What makes it worth reading is
that every number underneath it is on screen, labelled with where it came from.

## The three routes

| Route | Reads | Measures | Assumes |
| --- | --- | --- | --- |
| **Road load** | Engine speed, throttle, the gearing | At the **wheels** | Mass, drag area, rolling resistance |
| **Air** (speed density) | Manifold pressure, charge temperature, mixture | At the **crank** | Cylinder filling, BSFC |
| **Injectors** | Pulse width, mixture, supply voltage | At the **crank** | Injector flow, dead time, BSFC |

**Road load** is the one to trust first. It measures the car, not the engine, so
it is immune to everything the tune might be wrong about.

**Air** and **injectors** both work forward from fuel: they arrive at a fuel mass
flow and divide by a brake specific fuel consumption. That makes the *height* of
those two curves depend entirely on the BSFC assumption, while their *shape* does
not depend on it at all.

**Where they disagree, the shape of the disagreement says which input is wrong.**
Two routes agreeing on shape but not height is a BSFC or an injector flow. Road
load disagreeing with both across the whole range is usually the gear or the
mass. Road load agreeing at the bottom and drifting at the top is usually the
drag area.

> **NOTICE:** The three do not average. Averaging a good measurement with a bad
> one produces a worse measurement that looks more confident. Read them
> separately and use the disagreement.

## Requirements

| | |
| --- | --- |
| A log | With engine speed, and a full-throttle pull of at least 2 s and 1,500 rpm in it |
| A throttle channel | Otherwise nothing can confirm the pedal was down |
| The gearing | Either a road speed channel, or the gear told to it |
| For **air** | Manifold pressure, charge temperature and a mixture channel |
| For **injectors** | Injector pulse width and a mixture channel |

Only the first two are needed to draw anything. Each further route appears when
the channels it needs are present.

A road speed channel is **not** required. Without one the gear cannot be measured
and is worked out instead — see [Choosing the gear](#choosing-the-gear).

## How to run it

1. Open a log.
2. **Tools ▸ Dyno…**
3. Pick a pull from the drop-down at the top.

**Expected result:** up to three curves, power solid and torque dashed, over a
shared engine-speed axis. Under the chart: the peaks, what the figure rests on,
and a list of everything that was assumed rather than measured.

Pulls are offered **widest first**, not in the order they happened, so the one
worth reading is the one already selected.

## What you have to type

The left column is only the things no recording can state. Everything else is
read out of the log and the tune it carries.

| Field | Units | Notes |
| --- | --- | --- |
| **Mass** | kg | Everything aboard, driver included |
| **Final drive** | ratio | See the warning below |
| **Gear ratios** | ratios | Comma-separated, first gear first |
| **Tyre** | — | A size such as `245/40R18`, **or** a measured rolling diameter such as `648 mm` |
| **Driveline loss** | % | Used only to convert wheel figures to crank ones |
| **Displacement** | litres | For the air route |
| **Injectors** | cc/min | Each, on a bench, at the pressure they were flowed at |
| **BSFC** | lb/hp·h | Divides both fuel routes |
| **Cylinder filling** | % | Volumetric efficiency, used only when nothing in the log measured it |

> **WARNING:** **The final drive and the tyre are not separable.** The log only
> ever sees the two multiplied together, as road speed per engine revolution. A
> differential 6% taller and a tyre 6% smaller are the same log. If the routes
> will not reconcile at the differential you believe you have, measure the
> rolling diameter — roll the car one revolution and measure — and enter that,
> rather than adjusting whichever number is easier to change.

**What you type is remembered**, in [`vehicle.json`](Configuration#vehiclejson).
Only fields you actually changed are kept, so a field you left alone goes on
following whatever tune is in front of it instead of carrying one car's weight
into the next log. **Forget what I entered** puts every field back.

## What the recording knows

Underneath the fields is a table of every figure the estimate rests on, each
tagged with where it came from:

| Tag | Meaning |
| --- | --- |
| **log** | A channel in the recording |
| **tune** | The tune embedded in the log |
| **entered** | Typed into the fields above |
| **assumed** | A default nobody has confirmed |
| **missing** | Not available at all |

This is the part worth reading before the horsepower. A figure resting on
seventeen measurements and two assumptions is worth quoting; the same figure
resting on two measurements and seventeen assumptions is not, and from the
outside the two are identical.

On a MegaSquirt log the tune supplies rather a lot of it — cylinder count,
injector dead time and its correction against supply voltage, how many times each
injector opens per engine cycle, vehicle weight, frontal area and tyre diameter.

> **NOTICE:** The final drive in the tune is reported but **not applied** over
> what you entered. It is the one figure a tuning program is routinely left at
> its default, and it scales every road speed. Compare the two and decide.

## Choosing the gear

Effective mass includes the rotating parts, and those are geared: their
contribution scales with the **square** of the ratio. Getting the gear wrong is
therefore not a small error, and it is wrong in a direction that looks plausible.

The **Gear** drop-down has three behaviours:

- **A road speed channel exists.** The ratio is measured directly from engine
  speed against road speed and matched to a gear. Nothing is assumed.
- **Work it out** (the default when there is no road speed). Each gear is tried
  in turn and scored on how closely the road-load route then agrees with the air
  route. The one that agrees wins, and the readout says by how much, together
  with how far off the next-nearest gear was. A wide margin is a real result; a
  narrow one is a coin toss and says so.
- **Told outright.** Pick the gear yourself. Use this when you know it.

> **NOTICE:** Working the gear out requires the air route, which requires
> manifold pressure, charge temperature and mixture. Without those and without a
> road speed channel, the gear has to be told.

If you do not know your gear ratios, an upshift measures them: engine speed
immediately before and immediately after a shift is the ratio step between those
two gears, and no road speed channel is needed for that either.

## Reading the sheet

Power is solid, torque dashed, both on **one axis**. That is deliberate, and it
would be wrong almost anywhere else.

The two are not independent measurements. Torque in pound-feet is horsepower
times 5,252 divided by engine speed, so they are the same measurement expressed
twice — and they therefore cross at exactly **5,252 rpm** on every dyno sheet
ever printed. Sharing the axis is what makes that crossing visible, and the
crossing is a free check on the arithmetic behind the lines. The marker is drawn
where it must fall; if a curve crosses somewhere else, something is wrong.

Under the chart:

- **The peaks**, at the wheels and at the crank, and the crossover.
- **The basis** — mass, gearing, air, and where road speed came from.
- **The cautions** — one line per assumption, per route.

Curves are drawn at even steps of engine speed rather than at the log's own
samples. Samples are evenly spaced in *time*, and a pull is not: engine speed
climbs fastest where the engine is strongest, so a curve drawn straight from the
samples is bunched at the top and thin exactly where the engine is doing least.

**A half fitting window is trimmed from each end.** The derivative needs readings
either side of the point it describes, and at the two ends of a pull there are
none on one side. So take a pull a few hundred rpm past wherever your interest
ends — running to the limiter already does this.

## Wheels or crank

The road-load route measures at the **wheels**, because that is where the tyres
meet the road. The two fuel routes measure at the **crank**, because that is
where the fuel burns.

**Driveline loss** converts between them, and it is the one number in the whole
exercise that nothing can measure from a log. A percentage is the convention;
it is also a simplification, since real losses are closer to a fixed drag plus a
proportion. Treat a crank figure as a wheel figure with an assumption bolted on.

> **NOTICE:** Never compare a wheel figure with a crank one. Most published
> horsepower is at the crank; most rolling-road figures are at the wheels. The
> readout gives both so that a comparison is at least made against the right one.

## Air and corrections

Air density moves with the weather, and an engine makes power in proportion to
the air it can swallow. A cold high-pressure morning carries roughly ten per cent
more air per litre than a hot afternoon at altitude — larger than most of the
changes anybody makes a run to measure.

Where the log carries a barometer, it is used. This matters: four and a half
thousand feet is about a fifth of the air, and assuming sea level there would
overstate the correction badly.

Two published corrections exist:

| Standard | Reference conditions |
| --- | --- |
| **SAE J1349** | 990 mbar dry, 25 °C |
| **DIN 70020** | 1013 mbar dry, 20 °C — reads higher than SAE, always |

Both are only defined for correction factors between **0.93 and 1.07**. Outside
that band the correction is extrapolating past what the standard covers.

> **WARNING:** **Neither standard applies to a boosted engine.** They correct for
> the air an engine draws in naturally; a turbocharger targeting a boost pressure
> compensates for thin air by working harder, so correcting the result as well
> counts the same recovery twice. No correction is applied to a boosted engine.

## Why a pull was not offered

A stretch of log has to survive every one of these to be offered:

| Rejected because | Threshold |
| --- | --- |
| Engine speed was not climbing | Under 50 rpm/s, fitted |
| Too short to differentiate | Under 2 s |
| Too little of the rev range | Under 1,500 rpm |
| Sampled too slowly | Under 10 Hz |
| Not at full throttle | More than 4% under the log's own full throttle |

Full throttle is taken from **the log's own maximum**, not from a hundred. A
throttle channel reaches wherever its calibration puts it — plenty of controllers
stop at 96, and one reporting a voltage may top out anywhere — so a fixed
threshold finds nothing on one log and everything on another. The reference is
the fifth-highest reading rather than the highest, so one spike cannot set it.

A log where the pedal never passed **70%** is treated as having no full throttle
at all, rather than having its gentle maximum called a pull.

Pulls that pass but have something wrong with them are still offered, with the
problem recorded against them: the throttle came off part way, the ratio drifted
(a slipping converter, or the tyres), the gear matched nothing, or there was no
road speed to check the gear against at all.

## Settings

These are the values the analysis uses. They are not exposed in the window; they
are listed because the cautions refer to them.

| | Default | Units | What it is |
| --- | ---: | --- | --- |
| Fitting window | 0.5 | s | How much log each derivative is fitted over |
| Rung spacing | 100 | rpm | How far apart the drawn points are |
| Trim | 0.5 | windows | Left off each end of the pull |
| Gear tolerance | 8 | % | How far a measured ratio may sit from a gear |
| Ratio drift | 2.5 | % | How far the ratio may wander before it is called out |
| Assumed humidity | 50 | % | Where the log carries no humidity |

The **fitting window** is a local weighted quadratic against the real timestamps,
not a fixed number of samples. Log intervals are not even — on a typical MS2 log
somewhere between 6 and 14 per cent of consecutive samples share a timestamp
exactly — and a filter that assumes even spacing quietly produces a derivative
that is wrong wherever the spacing is not.

Vehicle defaults, used until something better is available:

| | Default |
| --- | ---: |
| Kerb mass | 1,400 kg |
| Occupants | 80 kg |
| Drag area (CdA) | 0.68 m² |
| Rolling resistance | 0.0125 |
| Wheel inertia | 4.5 kg·m² |
| Engine inertia | 0.20 kg·m² |
| Final drive | 4.10 |
| Tyre | 245/40R18 |

## What it will not do

- **It is not a dyno.** It cannot hold a steady state, so it says nothing about
  part throttle, and it measures one pull rather than a repeatable run.
- **It does not measure drag area or rolling resistance.** Both are entered or
  assumed. On a hard pull they take only a small share of the effort — the
  cautions state what share, per pull — so an approximate figure costs little.
  Measuring them properly needs a coastdown run in both directions, which
  separates grade from rolling resistance; that is not yet in the window.
- **It does not know about grade.** A road that is not level adds or removes a
  force the analysis attributes to the engine. One per cent of grade on a 1,500 kg
  car is around 9 hp at 50 mph. Use a level road.
- **It cannot separate the differential from the tyre.** See the warning above.
- **It does not write anything to the ECU.**

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| "No pull is drawn" | No stretch of log passed the tests. See [Why a pull was not offered](#why-a-pull-was-not-offered) |
| No pulls found at all | No throttle channel, or the pedal never passed 70% |
| Only one curve | The channels for the other routes are absent — check the knowledge table |
| Road-load curve missing | The gear is not known, and effective mass cannot be worked out without it |
| Every figure is far too low or too high | The gear, or the final drive against the tyre |
| The two fuel routes agree with each other but not with road load | BSFC — it divides both of them and neither measures it |
| A route reads about 60% high | A fuelling table being read as a true volumetric efficiency; the window detects this and says so |
| Curves rough at the bottom of the pull | A turbocharger still spooling. The derivative is honest; the engine really was doing that |
| The gear keeps coming out wrong | Enter the ratios, or tell it the gear outright |

> **NOTICE:** A MegaSquirt `VE1` channel above about 110% is a **fuelling table**,
> not a volumetric efficiency — a cylinder cannot fill to more than itself. Taken
> at face value it inflates the air and every horsepower with it. This is
> detected: the caution names the figure it reached and what was used instead.

## Related

- [Estimating power](User-guide) — the simpler single-figure estimate under **Tools ▸ Estimate power…**
- [Configuration](Configuration#vehiclejson) — where the car you type is stored
- [Command line](Command-line#capturing-parts-of-the-interface) — `--dyno`, which saves the window to a PNG
- [VE calibration](VE-calibration) — the other analysis that reads mixture against target
