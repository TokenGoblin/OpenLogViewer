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
- [Publishing the wiki](#publishing-the-wiki)
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

Full option list: [Command line](Command-line).

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
  cases â€” packed flag bytes, interleaved markers, scale and transform â€” without
  sample logs checked into the repository.
- **The preset, filter and settings stores are injected with temporary paths**, so
  tests never touch real user settings. Follow that pattern for anything new that
  persists.

There are also fakes for the hardware: `FakeController`, `FakeElm`,
`FakeElmOverTcp`, `FakeSubaru`, `FakeWriteConfirmation`. A new protocol should
come with one.

> **NOTICE:** **Passing tests are not the same as working against hardware.** The
> suite runs against fakes. Anything touching a real controller â€” a protocol
> change, a new firmware family, a write path â€” needs verifying on a board before
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
| `--categories` | How each channel was grouped â€” the quickest way to check the classifier |
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
[ini-and-channels.md](Firmware-definitions-and-channels) were taken.

Individual pieces of the interface can be captured too â€” menus, calculators, the
power estimate, fault codes. See [Command line â–¸ Capturing parts of the
interface](Command-line#capturing-parts-of-the-interface).

> **NOTICE:** `--insights` and `--screenshot` together hang. The findings window
> is modal, so the capture queued behind it never runs and the application never
> exits. Use them separately.

## Continuous integration

`.github/workflows/build.yml` runs on every push and pull request, on
`windows-latest`:

1. `dotnet restore` â€” separately, so a dependency problem reads as one
2. `dotnet build -warnaserror`
3. `dotnet test`
4. `dotnet list package --vulnerable --include-transitive` â€” fails the build on a
   known advisory, in its own step so a security problem reads as a security
   problem
5. `tools\build-wiki.ps1 -Check` â€” fails if `wiki/` no longer matches `docs/`,
   so the published wiki and the documentation cannot drift apart

Windows only, and not an oversight: the application is WPF and the core targets a
Windows SDK for the Bluetooth LE APIs, so a Linux runner would simply fail to
build.

## Building the installer

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add --global WixToolset.UI.wixext/5.0.2

installer\build.ps1
```

See [Installation â–¸ Build the installer](Installation#build-the-installer) for
the parameters and what the result contains.

## Coding conventions

Read a neighbouring file before writing a new one; the house style is consistent
and visible. In short:

- **Nullable and implicit usings are on** in every project.
- **Comments explain why, not what.** The codebase leans heavily on this: a
  comment that restates the line above it is noise, and one that records why a
  clamp exists, or which hardware failure a workaround was written for, is the
  most valuable thing in the file. Several of the trickiest behaviours here â€” the
  ELM327 read-completion rules, the rolled `â€¦doz` axes, the batched-request
  death â€” are documented only in the comment beside them and in this
  documentation set.
- **XML doc comments on public types and members**, saying what the thing is for.
- **Units in names and in doc comments** wherever a number has one.
- **A new persisted setting** goes through `JsonSettingsFile`, gets a default that
  is applied when the value is missing or nonsensical, and is documented in
  [Configuration](Configuration).

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
   - `docs/configuration.md` â€” if a setting, default or file changed
   - `docs/command-line.md` â€” if an option changed
   - `docs/troubleshooting.md` â€” for realistic new failure modes
   - `src/OpenLogViewer.Core/Guide.cs` â€” **the in-app guide**, if the change is
     user-visible
   - `CHANGELOG.md`
   - `wiki/` â€” run `tools\build-wiki.ps1`. CI fails if it is out of date
4. **Keep terminology consistent with the interface.** If the menu says **Connect
   over SSM (Subaru)**, the documentation says that, not "the Subaru link".
5. **Verify on hardware** anything that touches a real controller, and say in the
   pull request what you verified it against.
6. **Do not document behaviour that does not exist,** and do not describe planned
   functionality as existing.

### The in-app guide

`src/OpenLogViewer.Core/Guide.cs` is the manual carried inside the application,
for the very good reason that this software is used in garages with no internet.

It is written as data â€” `GuideSection` and `GuideEntry` records â€” so it can be
searched across sections, themed with the rest of the window, and checked by a
test. `GuideTests` asserts that every section has entries and that no text was
left empty.

Keep entries **short**. The `docs/` set is where detail belongs; the guide is what
somebody reads standing next to a car.

## Publishing the wiki

A GitHub or GitLab wiki is a separate git repository with a flat page namespace.
Rather than keep a second copy of the documentation there â€” which is a fork
nobody notices until the two disagree â€” the wiki is **generated from `docs/`**.

```powershell
tools\build-wiki.ps1                       # GitHub, into wiki/
tools\build-wiki.ps1 -Flavour GitLab       # GitLab conventions
tools\build-wiki.ps1 -Check                # fail if wiki/ is out of date
```

| Parameter | Default | Description |
| --- | --- | --- |
| `-Flavour` | `GitHub` | `GitHub` or `GitLab`. Changes the home page and sidebar names, the footer, and blob URL paths |
| `-OutputDirectory` | `wiki` | Where to write. Point it straight at a cloned wiki repository to publish |
| `-RepositoryUrl` | The GitHub repository | Rewrites every repository URL, including ones written into the prose |
| `-Check` | â€” | Verifies `wiki/` matches what `docs/` would produce. Runs in CI |

### What it changes

Only names and links. **The page text is copied through untouched** â€” if
something reads wrong on the wiki, fix it in `docs/` and build again.

| | `docs/` | Wiki |
| --- | --- | --- |
| File name | `tune-editing.md` | `Editing-a-tune.md`, which displays as "Editing a tune" |
| Internal link | `user-guide.md#export` | `User-guide#export` |
| Link out of `docs/` | `../LICENSE` | An absolute repository URL |
| Navigation | GitHub renders the directory | `_Sidebar.md`, and `_Footer.md` on GitHub |

Page names are mapped explicitly rather than derived from each heading, because
the headings carry punctuation a page name should not: "AI agent access (MCP)"
and "Firmware definitions, channels and roles" would give page names with
brackets and a comma that every link then has to URL-encode.

### First publish, GitHub

The wiki repository does not exist until the first page is created **in the
browser** â€” until then `git clone` returns "Repository not found".

1. On the repository, enable **Settings â–¸ Features â–¸ Wikis** if it is off.
2. Open the **Wiki** tab and **Create the first page**. Save anything; it is
   about to be overwritten.
3. Then:

```powershell
git clone https://github.com/TokenGoblin/OpenLogViewer.wiki.git
tools\build-wiki.ps1 -OutputDirectory ..\OpenLogViewer.wiki

cd ..\OpenLogViewer.wiki
git add -A
git commit -m "Generate the wiki from docs/"
git push
```

**Expected result:** the Wiki tab shows the documentation with a sidebar. The
landing page is `Home`.

### First publish, GitLab

GitLab creates the wiki repository on demand, so there is no browser step.

```powershell
git clone https://gitlab.com/<namespace>/OpenLogViewer.wiki.git
tools\build-wiki.ps1 -Flavour GitLab `
    -RepositoryUrl https://gitlab.com/<namespace>/OpenLogViewer `
    -OutputDirectory ..\OpenLogViewer.wiki

cd ..\OpenLogViewer.wiki
git add -A
git commit -m "Generate the wiki from docs/"
git push
```

### Updating it

Rebuild and push again. The generator clears the output's `.md` files first, so
a page deleted from `docs/` is deleted from the wiki rather than left behind.

> **NOTICE:** **Do not edit the wiki in the browser.** An edit made there is
> overwritten by the next build. The footer on every GitHub page and the sidebar
> on GitLab both say so. Edit `docs/` and rebuild.

## Releasing

1. Update `<Version>` in `src/OpenLogViewer.App/OpenLogViewer.App.csproj`.
   **Three parts only** â€” Windows Installer ignores the fourth when deciding
   whether one build supersedes another, and a four-part version produces
   releases that silently refuse to upgrade each other.
2. Update `CHANGELOG.md`.
3. Check the documentation set against the release â€” see the contributing
   checklist above.
4. `installer\build.ps1`. The version comes from the application's own
   `<Version>` unless overridden, so the installer and the thing it installs
   cannot disagree.
5. Attach the MSI to the release.

The build is not code-signed. SmartScreen will warn on first run until a
code-signing certificate is obtained; that is a certificate problem, not a code
change.

## Related

- [Architecture](Architecture)
- [The MLG log format](MLG-log-format)
- [Firmware definitions and channels](Firmware-definitions-and-channels)
- [AI agent access (MCP)](AI-agent-access-MCP)
