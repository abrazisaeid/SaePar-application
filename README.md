# SaePar Tunnel 2.0 Preview 15 — Cross Platform

SaePar Tunnel is being migrated from the Windows WPF client to a shared .NET MAUI solution for Windows, Android and iOS.

## Toolchain
- .NET 9
- Visual Studio 2022 17.12+
- .NET Multi-platform App UI development workload
- Recommended project path on Windows: `C:\SPT`

Open `SaeParTunnel.CrossPlatform.sln`.

## Shared features already in the MAUI project
- Epodonios/v2ray-configs Get Config
- VLESS / VMess / Trojan / Shadowsocks parsing
- Deduplication and persistent profile storage
- Search, protocol and health filters
- Parallel testing, progress, speed, ETA and cancellation
- Website/application whitelist settings model
- Shared Xray configuration builder
- Responsive dark MAUI UI

## Mobile-performance changes retained
- Only 120 config cards are rendered initially on Android/iOS; use **نمایش 120 مورد بعدی** to page forward.
- Range collection updates remove thousands of individual UI notifications.
- GitHub config extraction runs away from the UI thread.
- Bulk-test progress UI is throttled.
- Configs page now includes live status, ActivityIndicator, progress and cancel controls.
- Runtime errors display an alert instead of silently changing a label on another tab.
- Compact JSON storage reduces profile database I/O.

See `FIX-PREVIEW8-ANDROID-RESPONSIVENESS.md`.

## Platform status

### Windows
Real Xray process backend is present: download/prepare Xray, full proxy test, connect/disconnect and Windows System Proxy handling.

### Android
The MAUI application itself, GitHub/config management, filters, persistence and endpoint tests are present. **Android now includes a real device VPN backend.** The app bundles the official XTLS/libXray Android AAR and connects Android `VpnService` TUN traffic to the selected Xray outbound. Android tests also use libXray for a real full-proxy probe.

### iOS
The shared app is present. A real VPN requires a NetworkExtension Packet Tunnel provider + libXray Apple framework and a Mac/Xcode signing environment.

## Next native milestone
1. Device-test and harden the Android VPN backend across vendors/Android versions.
2. Add Android runtime traffic counters and connection diagnostics.
3. Add the iOS Packet Tunnel extension on a Mac with NetworkExtension signing.

## Previous migration fixes retained
- .NET 9 for Visual Studio 2022
- short Android build output paths to avoid XA5301/MAX_PATH
- unpackaged Windows debug target
- application resources load before pages are resolved from DI


## Preview 10 retained
Home includes a healthy-server Picker sorted by ping. Each item shows latency/protocol/name/endpoint, can be retested before connection, and can be switched before pressing Connect. Windows Xray bootstrap now has proxy/direct fallback plus manual `xray.exe` browse in Settings.


## Preview 11
- Fixes Windows `failed to listen TCP on 10809` by selecting free SOCKS/HTTP ports automatically and retrying bind conflicts.
- Removes the separate current-connection Ping/Test panel and dedicated probe inbound.
- Adds Configs sorting by newest/oldest added, ping, test time and name.
- Adds checkbox multi-selection and bulk test of only the checked profiles.
- Shows each profile's first-added timestamp in the Configs list.

See `FIX-PREVIEW11-PORT-SORT-MULTITEST.md`.


## Preview 15 — Android VPN

- Real Android `VpnService` foreground service with IPv4/IPv6 TUN.
- Official XTLS/libXray v26.7.28 AAR bundled in the project.
- Stable TUN fd bridge for tests + reconnects.
- `VpnService.protect()` registered for Xray outbound sockets to prevent VPN loops.
- Android full-proxy latency test through a temporary Xray SOCKS inbound.
- Android application whitelist uses `addAllowedApplication()`.
- Website whitelist is enforced by Xray domain routing inside the TUN.
- First Connect asks for Android's standard VPN consent dialog.

See `ANDROID-VPN-PREVIEW12.md` for implementation details.
