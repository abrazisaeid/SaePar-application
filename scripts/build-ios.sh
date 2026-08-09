#!/usr/bin/env bash
set -euo pipefail
# Run on macOS (or build through a paired Mac from Visual Studio).
dotnet workload install maui
dotnet build ./src/SaeParTunnel.App/SaeParTunnel.App.csproj -f net9.0-ios -c Debug
