# SaePar Tunnel v2.0.22

## Downloads and installation

- Android: install `SaeParTunnel-2.0.22-android.apk` on Android 7.0+ (arm64 or x86_64). The APK uses the existing release signing certificate, so it can update v2.0.21.
- Windows: extract **all** files from `SaeParTunnel-2.0.22-windows-x64.zip`, then run `SaeParTunnel.App.exe`. The x64 .NET/Windows App SDK runtimes and official Xray v26.7.28 engine are included.
- Alternatively run `SaeParTunnel-2.0.22-windows-x64.exe` to extract the complete app into a versioned folder and create Desktop/Start menu shortcuts, without administrator permissions.
- SHA-256 checksums are provided in `SHA256SUMS.txt` beside the downloads.

## Changes

- A simpler connection page: search, server selection, ping, connect and disconnect; advanced controls are on the second tab.
- Ping can retest the selected server and validate an active connection without disconnecting it.
- Simple search aims for five validated working configurations, prioritizes recent successful server groups and diverse endpoints, and uses rolling test workers with shared endpoint prechecks.
- Recent cached results can avoid unnecessary subscription downloads. Source fetching runs concurrently with bounded deadlines and direct/proxy fallback.
- Updated parsing and Xray compatibility checks reduce configurations that fail before connection testing.
- Old servers are removed only after repeated failures and a recent successful network test; a single missed ping does not delete a server.
- Cancellation preserves untested servers and stops counting canceled probes as failures. Profile loading, parsing and JSON storage run away from the UI thread.

## Verification

- All 61 Core tests pass.
- Signed Android Release publish and self-contained Windows Release publish complete.
- Android package ID `com.saepar.tunnel`, version `2.0.22`, version code `42`; APK signature verifies and certificate matches v2.0.21.
- All 602 Windows ZIP file contents match the published files; EXE, native UI runtime, .NET runtime, Xray and its license are present. No private profiles/settings or signing keys are included.
- Engine archive SHA-256 is pinned and checked during packaging.
- The self-contained installation EXE verifies its embedded ZIP hash and all archive entries in `--verify` mode, without installing or launching the VPN application.
- Physical Android/iOS device operation and live public-proxy connectivity were not verified in this session. Availability and time to find five working servers depend on the user's network and subscription contents.
