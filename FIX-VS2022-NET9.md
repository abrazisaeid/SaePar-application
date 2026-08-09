# Visual Studio 2022 / .NET 9 fix

This preview targets **.NET 9**, not .NET 10.

## Required
- Visual Studio 2022 17.12 or newer
- .NET Multi-platform App UI development workload
- .NET 9 SDK / MAUI workload

## First build
1. Close all older SaeParTunnel solutions.
2. Extract this ZIP into a new folder.
3. Open `SaeParTunnel.CrossPlatform.sln`.
4. Delete `bin` and `obj` if they exist.
5. Restore NuGet packages.
6. Select **Windows Machine** and build `SaeParTunnel.App`.

If workloads are missing, run `scripts/setup-windows.ps1` from PowerShell.

## What changed from preview1
- `net10.0-*` -> `net9.0-*`
- Core library `net10.0` -> `net9.0`
- `Microsoft.Maui.Controls` pinned to `9.0.120`
- `Version` / `PackageVersion` explicitly unified at `2.0.1` across TFMs to prevent NU1105
- build scripts now target .NET 9
- `global.json` keeps the solution on the .NET 9 SDK family
