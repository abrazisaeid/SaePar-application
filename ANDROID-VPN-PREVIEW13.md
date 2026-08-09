# SaePar Tunnel Preview 13 — Android real VPN backend

Android is no longer a UI-only preview. This source tree bundles the official
XTLS/libXray Android AAR built by the upstream `v26.7.28` GitHub workflow and
connects it to Android `VpnService`.

## Android data path

`Application traffic -> Android VpnService TUN -> Xray TUN inbound -> selected VLESS/VMess/Trojan/Shadowsocks outbound -> Internet`

Key implementation files:

- `Platforms/Android/SaeParVpnService.cs`: foreground `VpnService`, TUN creation,
  Android per-app allow-list, connect/disconnect lifecycle.
- `Platforms/Android/AndroidTunnelService.cs`: MAUI/ITunnelService bridge, VPN
  permission flow, real libXray full-proxy tests, start/stop commands.
- `Platforms/Android/Java/com/saepar/tunnel/bridge/SaeParXrayBridge.java`:
  stable-fd bridge, socket protection, DNS resolver integration, libXray Invoke API.
- `Platforms/Android/Libraries/libXray.aar`: upstream native Android library.

## Stable TUN fd design

The bundled libXray/Xray-core release reads `XRAY_TUN_FD` when the embedded Go
runtime initializes. Config testing can initialize Go before a real VPN exists.
The Java bridge therefore reserves one stable descriptor backed by `/dev/null`,
sets `XRAY_TUN_FD` before the first libXray invocation, and uses `dup2()` to swap
the live VpnService TUN onto that same descriptor number. On disconnect it swaps
`/dev/null` back in, releasing the Android VPN while keeping the descriptor number
reserved for later reconnects.

## Whitelist behavior on Android

- Whitelist disabled: all device apps are eligible for the VPN (SaePar itself is
  excluded as an extra loop-prevention guard; libXray outbound sockets are also
  protected with `VpnService.protect()`).
- Application whitelist present: Android `VpnService.Builder.addAllowedApplication`
  limits the VPN to those selected packages.
- Website whitelist present: the TUN enters Xray, whitelisted domains route to the
  proxy outbound, and unmatched domains route Direct.
- If app and website whitelists are both present, only selected apps enter the VPN;
  within those apps, the website rules decide Proxy vs Direct.

## Testing

Android config testing is now a real full-proxy test: a temporary Xray SOCKS
inbound is started by libXray and an HTTP probe is sent through the selected
outbound. Successful tests are stored as `FullProxy/Working` and therefore appear
in the Home healthy-server picker.

For safety, temporary libXray test cores are not started while the Android VPN
core is active; disconnect first, run tests, then reconnect.

## Important validation note

This package was statically validated in the generation environment, including
XML, Java bridge compilation against the upstream libXray interfaces, AAR content
and checksums. The generation environment does not contain the .NET Android/MAUI
workload, so the final Android APK must be compiled and device-tested in Visual
Studio 2022 on the target machine.


## Android system behavior

This preview explicitly opts out of Android Always-on VPN because reconnecting after a process/device restart requires a selected profile payload from the app. Foreground-service promotion declares and supplies the Android 14+ `specialUse` type at runtime.
