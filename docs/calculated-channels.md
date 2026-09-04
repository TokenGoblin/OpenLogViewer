# Calculated channels

Define a new channel from the ones a log already has.

- [What they are](#what-they-are)
- [Adding one](#adding-one)
- [Expression syntax](#expression-syntax)
- [Referring to channels](#referring-to-channels)
- [Missing readings](#missing-readings)
- [Where they are stored](#where-they-are-stored)
- [Examples](#examples)
- [Troubleshooting](#troubleshooting)

---

## What they are

A **calculated channel** is a channel whose values are computed from other
channels rather than recorded.

Once built, it is an ordinary channel in every respect:

- Plottable
- Usable as a histogram or scatter axis, or as the value channel
- Available to data filters
- Included in a CSV export

Calculated channels are marked **ƒ** in the channel list.

## Adding one

1. Click **ƒ Add calculated channel** at the bottom of the channel sidebar.
2. Give it a name and an expression.

**Expected result:** the channel appears in the list, marked **ƒ**, and can be
ticked like any other.

**To verify:** plot it alongside its inputs and check the arithmetic at a few
points with the hover readout.

## Expression syntax

### Operators

| Group | Operators | Notes |
| --- | --- | --- |
| Arithmetic | `+` `-` `*` `/` `%` `^` | `%` is remainder, `^` is exponentiation |
| Comparison | `<` `<=` `>` `>=` `==` `!=` | Yield 1 for true, 0 for false |
| Logical | `&&` `\|\|` `!` | |
| Grouping | `( )` | |

### Functions

| Function | Arguments | Returns |
| --- | ---: | --- |
| `abs(x)` | 1 | Absolute value |
| `sqrt(x)` | 1 | Square root |
| `floor(x)` | 1 | Largest integer not greater than `x` |
| `ceil(x)` | 1 | Smallest integer not less than `x` |
| `round(x)` | 1 | Nearest integer |
| `round(x, digits)` | 2 | Rounded to `digits` decimal places, 0 to 15 |
| `log(x)` | 1 | Natural logarithm |
| `log10(x)` | 1 | Base-10 logarithm |
| `exp(x)` | 1 | e raised to `x` |
| `sign(x)` | 1 | −1, 0 or 1 |
| `pow(x, y)` | 2 | `x` raised to `y` |
| `min(a, b, …)` | 2 to 8 | The smallest |
| `max(a, b, …)` | 2 to 8 | The largest |
| `clamp(x, low, high)` | 3 | `x` held between `low` and `high` |
| `if(condition, then, else)` | 3 | `then` when `condition` is non-zero, otherwise `else` |

### Constants

| Constant | Value |
| --- | --- |
| `pi` | 3.14159265358979… |
| `e` | 2.71828182845905… |

Function and constant names are matched case-insensitively.

## Referring to channels

**Channel names need no quoting, even with spaces in them.**

Names are matched against the log's own, **longest first**, so `AFR Target 1`
wins over `AFR`. A match must end on a word boundary, so `MAPX` is not read as
`MAP` followed by an unexplained `X`.

This is why the expression below works without any quoting:

```text
AFR - AFR Target 1
```

## Missing readings

**A missing reading propagates.** If an input has no value at a sample, the
calculated channel has no value there either.

This includes propagation **through comparisons**. Returning "false" for a
reading that was never taken would let `if` choose a branch on the strength of
nothing.

**A result that is not finite becomes a gap** rather than an infinity, which
would otherwise take the channel's whole range with it — a single division by
zero would flatten every other sample against the axis.

## Where they are stored

`%APPDATA%\OpenLogViewer\math.json`.

Definitions are held **by name and expression**, so they apply to any log
carrying the channels they refer to. One that does not fit the open log is
reported in the sidebar rather than silently dropped.

## Examples

```text
AFR - AFR Target 1
```

How far the mixture is from target, at every sample. Plot it, or use it as the
value channel in a histogram to see where in the map the engine runs off target.

```text
RPM * Torque / 5252
```

Horsepower from torque, where the log carries a torque channel. 5252 is the
constant for lb·ft and RPM.

```text
if(Boost psi > 0, Boost psi, 0)
```

Boost with vacuum clipped to zero, so the trace does not spend most of the log
below the axis.

```text
clamp(100 * (Duty Cycle1 / 85), 0, 200)
```

Injector duty as a percentage of an 85 % ceiling, clamped so one spike does not
take the range with it.

```text
MAP - Baro
```

Gauge pressure from two absolute readings.

## Troubleshooting

| Symptom | Likely cause | What to check |
| --- | --- | --- |
| The channel is listed but has no data | An input channel is not in this log | The sidebar reports which definition did not fit |
| The channel is flat at zero | A comparison is being used as a value | Comparisons yield 1 or 0; wrap them in `if(…)` if you want a magnitude |
| The trace has holes | An input has gaps, or the result is not finite | Gaps propagate deliberately. Check for division by a channel that reaches zero |
| Everything else on the plot is squashed flat | This channel reached a huge value | Wrap it in `clamp(…)` |
| A name is not recognised | The channel name differs from the log's | Copy the name exactly from the channel list |
| `MAP` matched the wrong channel | A longer name shares the prefix | Names are matched longest-first; use the full name |

## Related

- [User guide ▸ Finding a moment](user-guide.md#finding-a-moment) — the same
  syntax, used as a search
- [Histogram and scatter ▸ Data filters](histogram-and-scatter.md#data-filters)
- [Configuration](configuration.md)
