# Subaru SSM

Reading a Subaru over its own protocol instead of OBD2.

- [What SSM is, and why you would use it](#what-ssm-is-and-why-you-would-use-it)
- [What it costs](#what-it-costs)
- [Requirements](#requirements)
- [The parameter file](#the-parameter-file)
- [Connecting](#connecting)
- [Parameter file reference](#parameter-file-reference)
- [Why no addresses are shipped](#why-no-addresses-are-shipped)
- [Limitations](#limitations)
- [Troubleshooting](#troubleshooting)

---

## What SSM is, and why you would use it

**SSM** (Subaru Select Monitor) is Subaru's own diagnostic protocol. Where OBD2
reads a fixed set of standardised sensor values, SSM reads **arbitrary memory
addresses inside the ECU**.

That is the whole point of it. SSM reaches what the ECU has *learnt* rather than
what it is *measuring*:

- Knock correction
- Fine knock learning
- IAM (ignition advance multiplier)
- Learnt fuelling trims

**None of that is in OBD2 at any speed.** These are the values that tell you
whether a Subaru is happy, and they are exactly the ones the standard does not
carry.

## What it costs

SSM reads **one address per request**, at about **146 ms each**.

| Parameters | Approximate update rate |
| ---: | ---: |
| 8 | 0.85 Hz |
| 12 | 0.57 Hz |
| 24 | 0.29 Hz |

Compare OBD2 at roughly 2 Hz to 3 Hz. SSM is slower, and it is meant to be: the
values it reaches move over minutes, not milliseconds.

**Keep the list to what you actually want to watch.** One address per request
makes the parameter list a budget.

## Requirements

| | |
| --- | --- |
| Vehicle | A Subaru that speaks SSM |
| Adapter | A serial or USB adapter on the vehicle's diagnostic connector |
| A parameter file | `ssm-parameters.json`, which **you supply** — see below |

There is no definition file to find. There is an address list, and it is yours to
fill in.

## The parameter file

**Where it lives:**

```text
%USERPROFILE%\OpenLogViewer\ECU definitions\ssm-parameters.json
```

**How to get one:** the file is written out with a two-entry template the first
time it is wanted, so there is something to edit rather than a format to guess
at. Reach it with:

**Connect ▾ ▸ Connect over SSM (Subaru) ▸ Edit the parameter list…**

That opens the definitions folder.

### The template

```json
{
  "version": 1,

  "parameters": [
    {
      "name": "Engine Speed",
      "address": "0x00000E",
      "bytes": 2,
      "units": "rpm",
      "scale": 0.25,
      "digits": 0,
      "low": 0,
      "high": 8000
    },
    {
      "name": "Coolant",
      "address": "0x000008",
      "bytes": 1,
      "units": "°C",
      "scale": 1,
      "offset": -40,
      "digits": 0,
      "low": -40,
      "high": 215
    }
  ]
}
```

Those two were confirmed on a running 2014 Crosstrek by reading them against the
same values over OBD2. They are worked examples of the format, **not** the
interesting ones.

## Connecting

1. Fill in `ssm-parameters.json` with the addresses you want.
2. Plug the adapter in and turn the ignition on.
3. **Connect ▾ ▸ Connect over SSM (Subaru)**, and pick the port.

**Expected result:** the status bar reads

```text
Live — SSM   •   8 parameters   •   <adapter>
```

and the title bar reads `Live: SSM — OpenLogViewer`. Each parameter appears as a
channel, categorised as `SSM · 0x0000NN`.

**To verify:** include one address you can check independently — engine speed at
`0x00000E`, say — and confirm it agrees with the tachometer or with an OBD2
session on the same car.

**It refuses more readily than an OBD2 connection does, and deliberately.** The
addresses are yours and every one of them may be wrong. A session that started
happily and showed a screen of dashes would be a worse outcome than one that will
not start and says which address was refused.

SSM has its own entry in the menu rather than being guessed at from the adapter's
name, because it is a deliberate choice with a real cost. Nobody should land on
it by accident.

## Parameter file reference

Each entry in `parameters` takes:

| Field | Required | Type | Default | Description |
| --- | --- | --- | --- | --- |
| `name` | Yes | string | — | The channel name. Must be unique within the file |
| `address` | Yes | string | — | Hex, with or without `0x`. Range `0x000000` to `0xFFFFFF` |
| `bytes` | No | integer | `1` | How many consecutive bytes, most significant first |
| `units` | No | string | `""` | Shown beside the value |
| `scale` | No | number | `1` | Multiplied by the raw value |
| `offset` | No | number | `0` | Added after scaling. A temperature is typically raw − 40 |
| `digits` | No | integer | `0` | Decimal places shown |
| `low` | No | number | `0` | Gauge lower bound |
| `high` | No | number | `255` | Gauge upper bound |
| `enabled` | No | boolean | `true` | Whether this parameter is actually read |

The value shown is `raw × scale + offset`.

**`enabled` earns its keep** because one address per request makes the list a
budget. A file can hold every parameter a car offers — around 160 on this
protocol — while a dozen are switched on. Changing what you are watching is then
a matter of moving a flag rather than finding an address and its scaling again.

**A bad entry is dropped rather than failing the file.** Somebody filling this in
by hand against a forum post will get one wrong, and losing every parameter
because the fourth has a typo would be a poor trade. The parameters that survive
are reported, and the count is what tells you something went missing.

An entry is dropped when:

- `name` is missing or empty
- `address` is missing, not valid hex, or outside `0x000000`–`0xFFFFFF`
- `name` duplicates one already read — a duplicate would give two channels the
  same column, and every preset and filter matching on it would find whichever
  came first

## Why no addresses are shipped

**The protocol is here; the addresses are not, and that is deliberate rather than
unfinished.**

What lives at which address is not something this application can discover. The
two addresses in the template were confirmed by reading them against the same
values over OBD2 — and that method only works for parameters OBD2 already has,
which are precisely the ones not worth reaching SSM for. Knock correction and the
learnt fuelling trims have nothing to check them against.

The published maps that do have them belong to other projects under licences this
one cannot take from:

- **RomRaider** is GPL-2.0, against this project's MIT.
- The widely-copied definition repository **declares no licence at all**, which
  grants nothing to anybody.

So the file is yours to fill in, from whatever source you judge appropriate for
your own vehicle — the same arrangement as the ECU definition files this
application already expects you to provide.

It also means this works on any Subaru rather than only the one it was written
against.

## Limitations

- **Read-only.** Nothing is written to the ECU over SSM.
- **Slow.** About 146 ms per address, one address per request.
- **No tune.** There is no tune to read or edit over SSM, so **Calibration** is
  not available.
- **You supply the addresses.** A wrong address produces a wrong number, and
  nothing here can tell you which.
- **Verified against one vehicle.** A 2014 Crosstrek, for two addresses.

> **NOTICE:** An address you took from a forum post for a different model year
> may point at something else entirely. Confirm at least one parameter you can
> check independently before trusting the rest.

## Troubleshooting

| Symptom | Likely cause | What to check |
| --- | --- | --- |
| **Connect over SSM** starts and immediately refuses | An address in the file was refused by the ECU | The message names the address. Remove or correct it |
| Fewer parameters than the file lists | Bad entries were dropped, or some are `"enabled": false` | The count in the status bar against the entries in the file |
| A parameter reads a plausible but wrong value | Wrong `scale`, `offset` or `bytes` | Compare against the same value over OBD2 where the standard carries it |
| A parameter is a constant | Wrong address, or the ECU does not populate it | Try a known-good address such as `0x00000E` (engine speed) first |
| The file will not load at all | Invalid JSON | The whole file is skipped on a JSON error. Check it in an editor that validates JSON |
| Two channels seem to have merged | Duplicate `name` values | Names must be unique; the second is dropped |
| The update rate is very low | Too many parameters enabled | About 146 ms each. Switch off what you are not watching |

## Related

- [Live connection](live-connection.md)
- [OBD2](obd2.md) — faster, standardised, and enough for most work
- [Command line](command-line.md) — `--connect-ssm <port>`
- [Configuration ▸ Where files go](configuration.md#where-files-go)
