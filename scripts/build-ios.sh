#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "The iOS build must run on macOS with Xcode installed." >&2
  exit 1
fi

SIMULATOR_RID="iossimulator-x64"
if [[ "$(uname -m)" == "arm64" ]]; then
  SIMULATOR_RID="iossimulator-arm64"
fi

"$REPO_ROOT/native/ios/fetch-libxray.sh"
dotnet workload install maui ios --skip-manifest-update
dotnet build "$REPO_ROOT/src/SaeParTunnel.App/SaeParTunnel.App.csproj" \
  -f net9.0-ios \
  -c Debug \
  -r "$SIMULATOR_RID"
