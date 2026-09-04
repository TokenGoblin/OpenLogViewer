# OBD2

Connecting to any standard vehicle through a cheap ELM327 adapter, with no
definition file and nothing set up in advance.

- [What OBD2 gives you](#what-obd2-gives-you)
- [Adapters](#adapters)
- [Connecting: USB or wired](#connecting-usb-or-wired)
- [Connecting: Bluetooth LE](#connecting-bluetooth-le)
- [Connecting: Wi-Fi](#connecting-wi-fi)
- [Speed, and why it is slow](#speed-and-why-it-is-slow)
- [Gauges](#gauges)
- [Fault codes](#fault-codes)
- [What has been verified](#what-has-been-verified)
- [Limitations](#limitations)
- [Troubleshooting](#troubleshooting)

---

## What OBD2 gives you

**OBD2** (On-Board Diagnostics, generation 2) is the diagnostic standard every
car sold in most markets since the mid-1990s must implement. **SAE J1979** fixes
what every parameter means, so the numbering, the scaling and the units are the
same on every compliant car, and the car itself reports which parameters it
answers to.

Plug a dongle into a vehicle nobody has ever tried this on and you get named,
scaled channels. This is the one connection here that needs nothing set up in
advance.

**An ELM327** is the near-universal chip (and the name of its command set) used by
almost every consumer OBD2 adapter. OpenLogViewer speaks to it in ASCII, the same
way a phone app does.

## Adapters

| Kind | How it appears | How to connect |
| --- | --- | --- |
| USB / serial, self-identifying | In **Connect ▾** as a COM port, named `OBDII`, `OBDLink`, `ELM327`, `Vgate` etc. | Pick it. It is recognised as an adapter automatically |
| USB / serial, generic | Windows describes it only as `USB-SERIAL CH340` | **Connect ▾ ▸ Connect as an OBD2 adapter** |
| Bluetooth LE | In **Connect ▾** with `(Bluetooth LE)` after the name | Pick it |
| Wi-Fi | **In no list at all** | **Connect ▾ ▸ Connect to a Wi-Fi OBD2 adapter** |

A generic `USB-SERIAL CH340` is indistinguishable from a tuning cable until
something talks to it, and the two want opposite opening moves. That is why it is
asked for rather than guessed.

## Connecting: USB or wired

1. Plug the adapter into the vehicle's OBD2 port and into the laptop.
2. Turn the ignition on.
3. **Connect ▾** and pick the adapter, or **Connect as an OBD2 adapter** for a
   generic cable.

**Expected result:** the toolbar reports a live OBD2 session with a parameter
count and a rate, around 2 Hz.

**To verify:** with the engine stopped, MAP and barometric pressure should read
the same value — around 86 kPa at moderate altitude, around 101 kPa at sea level.
They only agree if both decode formulas are right.

### Baud rate

On a wired adapter the speed is **found rather than assumed**. A genuine ELM327
ships at 38,400 bit/s and clones ship at whatever the batch was built with, so
these are tried in order:

| Order | Baud rate |
| ---: | ---: |
| 1 | 38,400 bit/s |
| 2 | 115,200 bit/s |
| 3 | 9,600 bit/s |
| 4 | 500,000 bit/s |

A Bluetooth adapter ignores the setting entirely. A wrong speed is reported
differently from a key left out, because the two need different things done about
them.

## Connecting: Bluetooth LE

**Most cheap adapters sold now are Bluetooth LE**, and this catches people out.

**Bluetooth LE** (Low Energy) has **no serial port profile**. These adapters
never become a COM port however long you wait — which is how a perfectly good
dongle comes to look broken.

They carry the same ASCII ELM327 conversation over two GATT characteristics
instead. OpenLogViewer lists them in the same menu as the serial ports, with
`(Bluetooth LE)` after the name.

**To connect:** pair the adapter in Windows Bluetooth settings first, then pick
it from **Connect ▾**.

There is no standard for which GATT service an ELM327 clone uses, so the known
ones are tried in turn and **each is proved by asking it something before being
used**:

| Order | Service |
| ---: | --- |
| 1 | `0xFFF0` |
| 2 | `0xAE00` |
| 3 | `0xFFE0` |
| 4 | Nordic UART |

The adapter this was verified against publishes two of them and answers on only
one.

## Connecting: Wi-Fi

A Wi-Fi dongle is **its own access point with a TCP socket behind it**. It becomes
no COM port, pairs with nothing, and appears in no list any program can build. An
address is the whole of what it is reached by.

### Steps

1. **Join the dongle's own Wi-Fi network.** On a Vgate iCar Pro this is `V-LINK`.
2. **Check Windows has stayed on it.** See the warning below.
3. **Connect ▾ ▸ Connect to a Wi-Fi OBD2 adapter**, and pick an address.

| Address | Used by |
| --- | --- |
| `192.168.0.10:35000` | Vgate iCar Pro and most dongles built like it. Verified |
| `192.168.4.1:35000` | Other Wi-Fi ELM327 clones. Widely reported, not verified here |

Any other address can be given with `--connect-wifi <address>`. See
[Command line](command-line.md).

> **CAUTION:** **A network with no route to the internet is one Windows treats as
> a mistake.** It will leave it for one that has some, often within seconds. The
> failure then lands on the dongle while the cause is a laptop that quietly went
> home. If the connection fails, re-check the Windows network list before
> blaming the adapter.

These adapters accept **one connection at a time**, so a phone app still holding
it is refused rather than queued. Close the phone app first.

## Speed, and why it is slow

**It is slow, and that is the protocol rather than this application.**

Every other ECU here hands over its whole realtime block in one exchange. OBD2
has no such thing: each parameter is a separate request and a separate wait.

| Link | Measured rate |
| --- | ---: |
| OBD2 through an ELM327 | 2.2 Hz to 2.7 Hz |
| A tuning cable | About 40 Hz |

Fine for watching a car. **No use for catching a misfire.**

The parameters a needle follows — RPM, speed, throttle, load, MAP — are asked for
every round, and the rest take turns. So the headline gauges stay live while the
fuel level updates when it gets to it.

### Batched requests

**Where the car allows it, six parameters are asked for in one request.** The cost
of OBD2 is round trips rather than bytes, and ISO 15765 lets a single mode 01
request carry up to six. A round of readings becomes two exchanges instead of six.

It is **probed, never assumed**, and the probe can only answer yes on evidence:

- **At least two parameters must come back.** A car that ignores the extras and
  answers the first looks exactly like a batched reply carrying one.
- **Only tried on a bus positively identified as CAN** — not merely one that
  failed to identify as slow. An unknown protocol is neither, and this request
  reaches a J1850 vehicle as a malformed one.
- **Three unanswered batches and it gives up** for the rest of the session. What
  is given up is the batching and never the channels: a request that failed says
  nothing about which sensors the car has.

### Adapters that cannot survive being asked

> **NOTICE:** A Vgate iCar Pro Wi-Fi does not *refuse* a batched request. It
> answers one, completely and on time, and then the TCP session is simply gone —
> every read afterwards returns nothing while the socket still reports itself
> connected.

So the batch that killed the link looks like a success, and what identifies it is
the silence that follows.

Learning this costs a dropped link, so **the verdict is written to the settings
file against that adapter** and a dongle with form is not probed again. The probe
is itself a batched request, and on such a dongle that one request is the whole of
the damage.

**Two bad links before it is believed,** because a link can also die from being
out of range or the key going off, and condemning a capable adapter on one of
those would cost the advantage permanently. A different dongle starts clean.

The record lives under `obd2BatchDeaths` in
`%APPDATA%\OpenLogViewer\settings.json`. See
[Configuration](configuration.md#settingsjson).

## Gauges

Switch to **Gauges**. OBD2 dials are drawn to the standard's own ranges, with two
deliberate differences from a tuning ECU's gauges:

- **No warning or danger bands.** OBD2 describes what a value *is* and never what
  a *safe* one would be.
- **The rev counter is drawn to 8,000 rpm**, not the 16,383.75 rpm the encoding
  permits. There is no way to ask a car for its redline, and a dial drawn to the
  counter's ceiling leaves every real reading in the first quarter.

## Fault codes

**Tools ▸ Fault codes…**, once connected to an OBD2 vehicle.

### Reading them

All three of the standard's lists are read, because they are three different
statements about the car:

| List | What it means |
| --- | --- |
| **Confirmed** | What lit the malfunction indicator lamp |
| **Pending** | Seen once, and the car does not yet believe it. Most monitors want the same fault on two consecutive drive cycles |
| **Permanent** | Cannot be erased by anything but the controller. That is what they are for |

Each code carries the SAE definition where it has one.

**Where a description is missing, the window says why rather than guessing.** The
manufacturer-specific ranges are the maker's to assign, so P1131 means one thing
on a Ford and something unrelated on a Toyota. A plausible description of the
wrong one is how somebody ends up buying a sensor they did not need.

On an OBD2 connection the **Calibration** tab shows fault codes too, and scans
the first time you switch to it. There is no tune on a standard vehicle to put
there instead.

A scan can be run while the session is polling. The adapter takes one command at
a time, so the gauges stop for a second or two while the car is being asked.

### Clearing them

> **WARNING:** **Erasing fault codes does not fix anything, and costs more than
> the codes.** Mode 04 also clears the freeze frame — the one record of what the
> engine was doing at the moment the fault occurred, and the most useful thing
> there is for an intermittent — along with the oxygen sensor results and the
> readiness monitors. **A car cleared this morning cannot pass an emissions test
> this afternoon whatever its condition,** because it no longer has evidence that
> its monitors ever ran.

The confirmation says all of that. Permanent codes are read back afterwards and
reported. Most cars refuse the request with the engine running.

Clearing fault codes is **not** available to a connected AI agent, for this
reason. See [AI agent access (MCP)](mcp-server.md).

## What has been verified

| Test | Result |
| --- | --- |
| Bluetooth LE `ELM327 v1.5`, engine stopped | 24 parameters at 2.19 Hz, connected in five seconds. MAP and barometric pressure both read 86 kPa, which they only do if both formulas are right |
| OBDLink r2.6, engine running | 25 parameters at 2.7 Hz, none falling silent |
| MAP range, engine running | 20 kPa at idle, 87 kPa on an unloaded throttle blip against a barometric 86 kPa. An unloaded blip brings manifold pressure up to atmospheric and no further, so that is the whole range checked rather than the single point a stopped-engine test covers |
| Fuel trim scale | Every reading landed on an exact raw byte, which fixes the scale |
| Fuel trim offset | Long-term trim sat at +1.56 %, two counts off the 128 that means "no correction". A wrong offset would leave a healthy engine showing a large standing correction |
| Lambda | Oscillated 0.97 to 1.22 about 1.00, which is closed loop doing what closed loop does |

## Limitations

- **About 2 Hz.** Not fast enough for transient work.
- **No tune.** A standard vehicle has no tune to read, so **Calibration** shows
  fault codes instead.
- **No writing.** OpenLogViewer never writes to an OBD2 vehicle except to clear
  fault codes, and only when you confirm it.
- **Parameters vary by car.** The car reports which ones it answers to. A
  parameter the car does not support simply does not appear.
- **Manufacturer-specific codes are not decoded.** See above.

## Troubleshooting

| Symptom | Likely cause | What to check |
| --- | --- | --- |
| A Bluetooth adapter never appears as a COM port | It is Bluetooth LE, which has no serial port profile | It will not, ever. Look for it in **Connect ▾** with `(Bluetooth LE)` after the name |
| A Wi-Fi adapter appears in no list | Correct — it is an access point, not a device | Join its Wi-Fi, then **Connect ▾ ▸ Connect to a Wi-Fi OBD2 adapter** |
| Wi-Fi connection times out | Windows left the dongle's network for one with internet | Re-check the Windows network list; reconnect to `V-LINK` or equivalent |
| Wi-Fi connection is refused | A phone app still holds the single allowed connection | Close the phone app |
| "Nothing on COM*n* answered as an OBD2 adapter" | Wrong port, or it is a tuning cable | Confirm the port; try **Connect as an OBD2 adapter** for a generic CH340 |
| Connection succeeds but no channels | Ignition is off, or the car is not responding on the bus | Turn the ignition on. Some vehicles need the engine running |
| The link dies a second after connecting | A dongle that cannot survive a batched request | It is recorded after two occurrences and not probed again. A different dongle starts clean |
| Every reading is one command late | The adapter ignores `ATE0` and echoes commands | Handled automatically; if it persists, report the adapter model |
| Readings stop while scanning fault codes | Expected — the adapter takes one command at a time | Wait a second or two |

## Related

- [Live connection](live-connection.md)
- [Configuration](configuration.md)
- [Command line](command-line.md)
- [Troubleshooting](troubleshooting.md)
