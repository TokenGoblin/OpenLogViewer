# Development

Building, testing, and contributing.

- [Getting set up](#getting-set-up)
- [Building](#building)
- [Running](#running)
- [Testing](#testing)
- [The dump tool](#the-dump-tool)
- [The probe tool](#the-probe-tool)
- [Screenshots and captures](#screenshots-and-captures)
- [Continuous integration](#continuous-integration)
- [Building the installer](#building-the-installer)
- [Coding conventions](#coding-conventions)
- [Contributing](#contributing)
- [Releasing](#releasing)

---

## Getting set up

| Requirement | Notes |
| --- | --- |
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | Builds everything |
| Windows | The application targets `net10.0-windows10.0.19041.0` |
| WiX 5.0.2 | Only to build the MSI |

```powershell
git clone https://github.com/TokenGoblin/OpenLogViewer.git
cd OpenLogViewer
dotnet build OpenLogViewer.slnx -c Release
```

`OpenLogViewer.Core` targets plain `net10.0` with no WPF reference, so it and its
test suite build on any platform. The WPF application does not.

## Building

```powershell
dotnet build OpenLogViewer.slnx -c Release
```

CI builds with `-warnaserror`. Locally it does not, deliberately: a warning should
be something you can see and keep working past, and on the way to a merge it
should not be.

## Running

```powershell
dotnet run --project src/OpenLogViewer.App -c Release
dotnet run --project src/OpenLogViewer.App -c Release -- "C:\logs\example.mlg"
```

Full option list: [Command line](command-line.md).

Errors from a scripted run go to `%TEMP%\openlogviewer-run.log`, because a WPF
application has no console attached.

## Testing

```powershell
dotnet test -c Release
```

**2,347 tests, in two suites:**

| Suite | Tests | What it covers |
| --- | ---: | --- |
| `OpenLogViewer.Tests` | 1,835 | The readers, the histogram and scatter, filters, tune axes, the tune model, the protocols, and every calculator |
| `OpenLogViewer.App.Tests` | 512 | The view model, driven end to end: write a log, open it, and exercise the channel list, presets, filters, histogram, live sessions, tune edits and the MCP tools the way the interface does |

Two things about how they are written are worth knowing before adding to them:

- **The MLG tests build synthetic log files in memory**, so they cover the awkward
  cases — packed flag bytes, interleaved markers, scale and transform — without
  sample logs checked into the repository.
- **The preset, filter and settings stores are injected with temporary paths**, so
  tests never touch real user settings. Follow that pattern for anything new that
  persists.

There are also fakes for the hardware: `FakeController`, `FakeElm`,
`FakeElmOverTcp`, `FakeSubaru`, `FakeWriteConfirmation`. A new protocol should
come with one.

> **NOTICE:** **Passing tests are not the same as working against hardware.** The
> suite runs against fakes. Anything touching a real controller — a protocol
> change, a new firmware family, a write path — needs verifying on a board before
> it is trusted. `docs/mcp-server.md` and `docs/ini-and-channels.md` record what
> has and has not actually been proven, and on what.

## The dump tool

`OpenLogViewer.Dump` decodes a log and prints a summary. It doubles as the
regression check for the readers, and it is the fastest way to see how a new
firmware's channels were understood.

```powershell
dotnet run --project tools/OpenLogViewer.Dump -c Release -- <log> [--channels] [--categories] [--tune]
```

| Option | Shows |
| --- | --- |
| `--channels` | Every channel with its units and range |
| `--categories` | How each channel was grouped — the quickest way to check the classifier |
| `--tune` | The tune axes found in the log |

## The probe tool

`OpenLogViewer.Probe` asks a vehicle what it will answer beyond the OBD2
standard, and writes down exactly what came back.

```powershell
dotnet run --project tools/OpenLogViewer.Probe -c Release -- [COM port] [--baud N] [--out transcript.txt] [--sweep]
dotnet run --project tools/OpenLogViewer.Probe -c Release -- --wifi [address|auto] [--out transcript.txt]
```

**This is a probe and not a feature.** Nothing is guessed at and then acted on:
every candidate is sent, every reply is recorded verbatim, and what any of it
means is decided afterwards by reading the transcript. The questions it asks are
plausible rather than known, and a tool that quietly interpreted them would turn a
plausible guess into a confident wrong answer.

> **NOTICE:** It is **read-only, and structurally so.** The SSM command set
> includes address writes (`0xB8`) and this sends none. Every request either reads
> or asks what is supported, and anything not on the allowed list is refused
> rather than trusted to be harmless.

## Screenshots and captures

The application renders itself to a PNG rather than being captured from another
process, because capturing from outside is unreliable under Desktop Window Manager
composition.

```powershell
OpenLogViewer.App.exe path\to\log.mlg --screenshot out.png
OpenLogViewer.App.exe path\to\log.mlg --pointer 0.42,0.55 --screenshot out.png
```

`--pointer` takes fractions of the plot area and places the cursor first, so the
hover readout appears in the capture.

Against a live controller:

```powershell
OpenLogViewer.App.exe --connect COM8 --settle 15000 --export <dir>
```

That writes the plot, the plotted channels and every channel as CSV once the
session has settled. It is how the bench measurements in
[ini-and-channels.md](ini-and-channels.md) were taken.

Individual pieces of the interface can be captured too — menus, calculators, the
power estimate, fault codes. See [Command line ▸ Capturing parts of the
interface](command-line.md#capturing-parts-of-the-interface).

> **NOTICE:** `--insights` and `--screenshot` together hang. The findings window
> is modal, so the capture queued behind it never runs and the application never
> exits. Use them separately.

## Continuous integration

`.github/workflows/build.yml` runs on every push and pull request, on
`windows-latest`:

1. `dotnet restore` — separately, so a dependency problem reads as one
2. `dotnet build -warnaserror`
3. `dotnet test`
4. `dotnet list package --vulnerable --include-transitive` — fails the build on a
   known advisory, in its own step so a security problem reads as a security
   problem

Windows only, and not an oversight: the application is WPF and the core targets a
Windows SDK for the Bluetooth LE APIs, so a Linux runner would simply fail to
build.

## Building the installer

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add --global WixToolset.UI.wixext/5.0.2

installer\build.ps1
```

See [Installation ▸ Build the installer](installation.md#build-the-installer) for
the parameters and what the result contains.

## Coding conventions

Read a neighbouring file before writing a new one; the house style is consistent
and visible. In short:

- **Nullable and implicit usings are on** in every project.
- **Comments explain why, not what.** The codebase leans heavily on this: a
  comment that restates the line above it is noise, and one that records why a
  clamp exists, or which hardware failure a workaround was written for, is the
  most valuable thing in the file. Several of the trickiest behaviours here — the
  ELM327 read-completion rules, the rolled `…doz` axes, the batched-request
  death — are documented only in the comment beside them and in this
  documentation set.
- **XML doc comments on public types and members**, saying what the thing is for.
- **Units in names and in doc comments** wherever a number has one.
- **A new persisted setting** goes through `JsonSettingsFile`, gets a default that
  is applied when the value is missing or nonsensical, and is documented in
  [Configuration](configuration.md).

## Contributing

1. **Branch from `main`.**
2. **Add tests.** Every behaviour here is testable without hardware; if yours is
   not, add a fake.
3. **Update the documentation in the same change.** A user-facing feature is not
   complete until the relevant pages are updated. Work out which of these are
   affected:
   - `README.md`
   - `docs/getting-started.md`, `docs/user-guide.md`
   - The feature's own page under `docs/`
   - `docs/configuration.md` — if a setting, default or file changed
   - `docs/command-line.md` — if an option changed
   - `docs/troubleshooting.md` — for realistic new failure modes
   - `src/OpenLogViewer.Core/Guide.cs` — **the in-app guide**, if the change is
     user-visible
   - `CHANGELOG.md`
4. **Keep terminology consistent with the interface.** If the menu says **Connect
   over SSM (Subaru)**, the documentation says that, not "the Subaru link".
5. **Verify on hardware** anything that touches a real controller, and say in the
   pull request what you verified it against.
6. **Do not document behaviour that does not exist,** and do not describe planned
   functionality as existing.

### The in-app guide

`src/OpenLogViewer.Core/Guide.cs` is the manual carried inside the application,
for the very good reason that this software is used in garages with no internet.

It is written as data — `GuideSection` and `GuideEntry` records — so it can be
searched across sections, themed with the rest of the window, and checked by a
test. `GuideTests` asserts that every section has entries and that no text was
left empty.

Keep entries **short**. The `docs/` set is where detail belongs; the guide is what
somebody reads standing next to a car.

## Releasing

1. Update `<Version>` in `src/OpenLogViewer.App/OpenLogViewer.App.csproj`.
   **Three parts only** — Windows Installer ignores the fourth when deciding
   whether one build supersedes another, and a four-part version produces
   releases that silently refuse to upgrade each other.
2. Update `CHANGELOG.md`.
3. Check the documentation set against the release — see the contributing
   checklist above.
4. `installer\build.ps1`. The version comes from the application's own
   `<Version>` unless overridden, so the installer and the thing it installs
   cannot disagree.
5. Attach the MSI to the release.

The build is not code-signed. SmartScreen will warn on first run until a
code-signing certificate is obtained; that is a certificate problem, not a code
change.

## Related

- [Architecture](architecture.md)
- [The MLG log format](mlg-format.md)
- [Firmware definitions and channels](ini-and-channels.md)
- [AI agent access (MCP)](mcp-server.md)
