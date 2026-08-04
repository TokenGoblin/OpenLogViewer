# Third-party notices

OpenLogViewer is MIT licensed — see [LICENSE](LICENSE). This file lists the
third-party code it is built on, and in particular the code that is
**redistributed** inside the installer.

It exists because the installer publishes a self-contained build: the MSI carries
the .NET runtime and the WPF libraries alongside the application, roughly 240
assemblies in total. Those are MIT licensed, and the MIT licence asks that its
copyright and permission notice travel with "all copies or substantial portions
of the Software". Shipping them with nothing that says where they came from would
not meet that.

## Redistributed in the installer

| Component | Licence | Copyright |
|---|---|---|
| .NET runtime and libraries (`dotnet/runtime`) | MIT | © .NET Foundation and Contributors |
| Windows Presentation Foundation (`dotnet/wpf`) | MIT | © .NET Foundation and Contributors |
| `System.IO.Ports` | MIT | © .NET Foundation and Contributors |
| `System.Management` | MIT | © .NET Foundation and Contributors |

Each carries the MIT terms, which are the same as this project's own — reproduced
in [LICENSE](LICENSE). Full per-component notices ship with the .NET SDK as
`THIRD-PARTY-NOTICES.TXT`.

## Build and test only — not redistributed

These are used to build or test the application and are not part of anything that
gets installed:

| Component | Licence |
|---|---|
| xUnit.net | Apache-2.0 |
| `xunit.runner.visualstudio` | Apache-2.0 |
| `Microsoft.NET.Test.Sdk` | MIT |
| `coverlet.collector` | MIT |
| WiX Toolset (installer build) | MS-RL |

## What is deliberately absent

**No GPL or otherwise copyleft code or data is included in this repository or in
anything it distributes.** That is worth stating rather than leaving to be
inferred, because two of the obvious sources for part of this work are copyleft
and were deliberately not used:

- **RomRaider** (GPL-2.0) and **FreeSSM** (GPL-3.0) both publish Subaru SSM
  parameter maps. Neither is copied here, and neither was read while implementing
  the protocol.
- The SSM support in this project ships **the protocol and not the addresses**.
  The two addresses in the built-in template — engine speed at `0x00000E` and
  coolant at `0x000008` — were established by measurement against a running
  vehicle and cross-checked against OBD2, before either project's definitions
  were consulted. The probe transcript in
  [`docs/probes`](docs/probes/2014-subaru-crosstrek-ssm.txt) records that, and the
  commit history preserves the order.
- Anyone wanting a fuller address map supplies it themselves in
  `ssm-parameters.json` in their own definitions folder. That file is theirs and
  never forms part of a release, so what they choose to put in it is governed by
  whatever licence they took it from — not by this project's.

**No ECU definition files are redistributed.** Firmware `.ini` definitions belong
to their authors — MegaSquirt, rusEFI, Speeduino and others — and the application
reads the ones already installed on the machine or placed in its definitions
folder. Nothing is downloaded and nothing is bundled.

## No network access

The application makes no network requests of any kind. There is no HTTP client,
no socket, and no telemetry. The only outward call in the codebase hands a
workspace folder or the project's own README URL to the shell, which is what
opens Explorer or a browser when you ask it to.
