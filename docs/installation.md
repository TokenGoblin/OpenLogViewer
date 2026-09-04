# Installation

- [Requirements](#requirements)
- [Install from the MSI](#install-from-the-msi)
- [What the installer does](#what-the-installer-does)
- [Uninstalling](#uninstalling)
- [Build and run from source](#build-and-run-from-source)
- [Build the installer](#build-the-installer)
- [Limitations](#limitations)

---

## Requirements

### To run the installed application

| | |
| --- | --- |
| Operating system | Windows 10 version 1809 (build 17763) or later |
| Architecture | x64 (an ARM64 build can be produced from source) |
| Runtime | None — the installer is self-contained |
| Download size | About 54 MB |

Nothing else is required to open a recorded log. A live connection additionally
needs a serial, Bluetooth LE or Wi-Fi adapter — see
[Live connection](live-connection.md).

### To build from source

| | |
| --- | --- |
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | Builds and tests the application |
| Windows | The application project targets `net10.0-windows10.0.19041.0` |
| [WiX Toolset 5.0.2](#build-the-installer) | Only needed to build the MSI |

`OpenLogViewer.Core` targets plain `net10.0` with no WPF reference, so the
readers and analysis can be built and tested on any platform. The WPF
application cannot.

## Install from the MSI

1. Download `OpenLogViewer-<version>-win-x64.msi`.
2. Run it.

> **NOTICE:** The installer is not code-signed. Windows SmartScreen shows
> "Windows protected your PC" the first time it runs. Choose **More info ▸ Run
> anyway**. Signing requires a code-signing certificate, not a change to the
> software.

**Expected result:** OpenLogViewer appears in the Start menu and opens to an
empty window with a channel sidebar on the left.

**To verify:** open **Help ▸ About OpenLogViewer**. It reports the installed
version.

## What the installer does

| | |
| --- | --- |
| Installs to | `%ProgramFiles%\OpenLogViewer` |
| Start menu shortcut | Yes |
| File associations | Registers `.mlg`, `.msl` and `.MaxxECU-Zip-log` under **Open with** only |
| Writes user data to | Nothing at install time |

The file associations are deliberately registered under `OpenWithProgids`
rather than as the default handler. OpenLogViewer appears in the **Open with**
list and never takes the double-click — anyone running this most likely has
TunerStudio installed, and those are its files.

Nothing is ever written next to the executable, so the application is content
installed read-only under Program Files. Settings go to `%APPDATA%`, and
recordings go to your user profile. See
[Configuration ▸ Where files go](configuration.md#where-files-go).

## Uninstalling

Use **Settings ▸ Apps ▸ Installed apps**, or Add/Remove Programs.

Uninstalling removes the program and **leaves `%USERPROFILE%\OpenLogViewer`
alone**, because those are your recordings. Settings under
`%APPDATA%\OpenLogViewer` are also left in place; delete that folder by hand if
you want a clean slate.

## Build and run from source

```powershell
git clone https://github.com/TokenGoblin/OpenLogViewer.git
cd OpenLogViewer
dotnet build OpenLogViewer.slnx -c Release
dotnet run --project src/OpenLogViewer.App -c Release
```

To open a log directly:

```powershell
dotnet run --project src/OpenLogViewer.App -c Release -- "C:\logs\2026-07-26_13.23.25.mlg"
```

**Expected result:** the application window opens with that log loaded.

Run the tests with:

```powershell
dotnet test -c Release
```

See [Development](development.md) for what the test suites cover and how to add
to them.

## Build the installer

WiX 5 is used rather than 6 or 7, which are gated behind the Open Source
Maintenance Fee and refuse to run without accepting its licence.

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add --global WixToolset.UI.wixext/5.0.2

installer\build.ps1
```

**Expected result:** `installer\out\OpenLogViewer-<version>-win-x64.msi`, about
54 MB.

| Parameter | Default | Values | Description |
| --- | --- | --- | --- |
| `-Version` | The `<Version>` in `OpenLogViewer.App.csproj` | Three-part, e.g. `0.2.0` | Version stamped into both the application and the MSI |
| `-Runtime` | `win-x64` | `win-x64`, `win-arm64` | Target architecture |
| `-OutputDirectory` | `installer\out` | Any path | Where the MSI is written |

Use a three-part version. Windows Installer ignores the fourth part when
deciding whether one build supersedes another, so a four-part version produces
releases that silently refuse to upgrade each other.

The build publishes self-contained and single-file. Self-contained is
deliberate: the people this is for plug a laptop into a car, often in a garage
with no internet, and "download a 60 MB runtime first" is the wrong thing to
say at that moment. It costs about 130 MB over a framework-dependent build.

## Limitations

- **Windows only.** The application uses WPF and Windows-specific APIs for
  serial port enumeration and Bluetooth LE.
- **Not signed.** SmartScreen warns on first run until a code-signing
  certificate is obtained.
- **Not trimmed.** WPF is not trim-safe, so the published size is near its
  floor.
- **No auto-update.** Install a new MSI over the old one; the three-part version
  makes it an upgrade rather than a second installation.
- **No portable build is published.** `dotnet publish` produces one if you need
  it, but it is not part of a release.
