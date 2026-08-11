# Architecture

```text
SaeParTunnel.Core (net9.0)
  Models / parsing / extraction / health testing / XrayConfigBuilder
           ^
SaeParTunnel.App (.NET MAUI)
  Pages / view models / persistence / platform tunnel coordination
           |
  ITunnelService
     |-- Windows: Xray process + Windows System Proxy
     |-- Android: VpnService + official libXray AAR
     `-- iOS: NETunnelProviderManager + Packet Tunnel extension
                                      |
                                      `-- official LibXray.xcframework
```

## Platform ownership

The shared app owns profile management, guided health testing, routing preferences and connection-state presentation. Each `ITunnelService` implementation owns platform permissions, tunnel lifecycle and post-connect internet validation.

Windows launches Xray as a child process and controls the system proxy. Android owns a TUN descriptor through `VpnService`. On iOS, the host app stores the selected profile in `NETunnelProviderProtocol`; `SaeParTunnel.iOS.Extension` receives that request, configures `NEPacketTunnelNetworkSettings`, finds the active utun descriptor and starts libXray with `env["xray.tun.fd"]`.

Android and iOS route public traffic through Xray while retaining direct access to private and link-local LAN ranges. iOS reports `Connected` to the UI only after Network Extension reaches the connected state and an HTTP request succeeds through the active tunnel.

Windows reuses `%LOCALAPPDATA%/SaeParTunnel`, so v1.x profiles/settings migrate automatically. Android and iOS use the MAUI app-data directory.

## Native dependencies

The Android binding restores a pinned official libXray AAR. The iOS binding uses the official XTLS/libXray `v26.7.28` Apple XCFramework and verifies its SHA-256 before extraction. Generated native binaries stay outside Git; CI and local build scripts restore them deterministically.
