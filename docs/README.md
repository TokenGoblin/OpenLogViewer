# OpenLogViewer documentation

OpenLogViewer is a Windows application for reading engine datalogs and for
connecting live to an engine control unit (ECU). It opens recorded logs, plots
and analyses them, and — on a supported controller — reads and edits the tune.

If you have never used it before, start with **[Getting started](getting-started.md)**.

The same material is also carried inside the application, offline, under
**Help ▸ How to use this app**. This set goes further: it documents defaults,
valid ranges, file formats and command-line options that the in-app guide
deliberately leaves out.

---

## Start here

| Page | What it covers |
| --- | --- |
| [Getting started](getting-started.md) | Install, open your first log, and confirm it worked |
| [Installation](installation.md) | Requirements, the installer, building from source |
| [User guide](user-guide.md) | Everyday operation: channels, the plot, presets, search, export |

## Analysis

| Page | What it covers |
| --- | --- |
| [Histogram and scatter](histogram-and-scatter.md) | Binning a log into a table, and plotting samples unaveraged |
| [VE calibration](ve-calibration.md) | Suggesting a fuel table from logged mixture against target |
| [Calculated channels](calculated-channels.md) | Defining new channels from existing ones; expression syntax |

## Live connection

| Page | What it covers |
| --- | --- |
| [Live connection](live-connection.md) | Connecting to a tuning ECU, recording, and gauges |
| [OBD2](obd2.md) | Any standard vehicle through an ELM327 adapter, including fault codes |
| [Subaru SSM](subaru-ssm.md) | Subaru's own protocol, and the parameter file you supply |
| [Editing a tune](tune-editing.md) | Tables, settings, sending, burning, and saved `.msq` files |

## Reference

| Page | What it covers |
| --- | --- |
| [Configuration](configuration.md) | Every setting, its default, its range, and where it is stored |
| [Command line](command-line.md) | All command-line options |
| [Troubleshooting](troubleshooting.md) | Symptoms, likely causes, and what to check |
| [AI agent access (MCP)](mcp-server.md) | Letting an AI agent drive the application |
| [Firmware definitions and channels](ini-and-channels.md) | How `.ini` files are found, read and mapped to channels |
| [The MLG log format](mlg-format.md) | The binary `MLVLG` container, field by field |

## For developers

| Page | What it covers |
| --- | --- |
| [Architecture](architecture.md) | How the code is laid out and how data flows through it |
| [Development](development.md) | Building, testing, the installer, and contributing |
| [Changelog](../CHANGELOG.md) | Release history |

---

## Conventions used in these pages

- **Bold** names an element you see on screen, spelled as the application spells
  it — a menu, a button, a checkbox, a status line.
- `Monospace` is something you type, or a file, path or value.
- A ▸ separates menu levels: **Tools ▸ Fault codes…**
- Units are always given. Where the application shows a value in the units the
  ECU reported, these pages say so.

## Safety

OpenLogViewer can change the tune in a connected ECU and commit that change to
flash. Each is behind its own confirmation that says what is about to happen.

> **WARNING:** A write takes effect immediately on a running engine. An
> incorrect fuel or ignition value can cause detonation, overheating or engine
> damage within seconds. Make changes with the engine stopped, or in small steps
> with the engine monitored, and read [Editing a tune](tune-editing.md) before
> the first write.

Tuning decisions made with this software are yours. See [LICENSE](../LICENSE).
