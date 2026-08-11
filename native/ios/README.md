# iOS Packet Tunnel

SaePar Tunnel supports a real device VPN on iOS 15 and later. The implementation has three parts:

- `SaeParTunnel.App` manages `NETunnelProviderManager`, user permission and verified connection state.
- `SaeParTunnel.iOS.Extension` configures routes/DNS and runs as an `NEPacketTunnelProvider`.
- `SaeParTunnel.iOSBinding` invokes the pinned official XTLS/libXray Apple XCFramework.

The app and extension both use the `packet-tunnel-provider` Network Extension entitlement. Their bundle identifiers are:

```text
com.saepar.tunnel
com.saepar.tunnel.packet-tunnel
```

## Build prerequisites

- macOS with a supported Xcode version
- .NET 9 SDK and MAUI/iOS workloads
- iOS 15 or later for real-device use
- Apple Developer access with the Network Extensions capability

Restore the native framework and build the current Mac architecture's simulator:

```bash
./native/ios/fetch-libxray.sh
./scripts/build-ios.sh
```

`fetch-libxray.sh` downloads `libxray-apple-cgo.zip` from XTLS/libXray `v26.7.28`, verifies SHA-256 `07f7ed7697277930e1c517755855950f594f41435b0dfc5917a66eea6278aeb9`, and extracts `LibXray.xcframework`. The extracted framework is intentionally ignored by Git.

## Signed IPA

Create explicit provisioning profiles for both bundle identifiers. Both profiles must contain `com.apple.developer.networking.networkextension` with `packet-tunnel-provider`, belong to the same Apple team and match the imported signing certificate.

After the profiles and signing identity are installed on the Mac:

```bash
export IOS_SIGNING_KEY='Apple Distribution: Example Company (TEAMID)'
export IOS_APP_PROVISIONING_PROFILE='APP_PROFILE_UUID'
export IOS_EXTENSION_PROVISIONING_PROFILE='EXTENSION_PROFILE_UUID'
./scripts/package-ios.sh v2.0.21
```

Set `IOS_SIGNING_KEYCHAIN` as well when the identity is stored in a non-default keychain. The packaging script validates the IPA archive, both bundle identifiers, the embedded Packet Tunnel extension, embedded profiles and the final code signature.

An Ad Hoc IPA only installs on devices whose UDIDs are included in its provisioning profiles. Use TestFlight or App Store distribution for general users.

## Routing behavior

The extension gives Xray the live utun descriptor through the root config value `env["xray.tun.fd"]`. IPv4 and IPv6 public traffic use the tunnel; RFC1918, IPv4 link-local, IPv6 unique-local and IPv6 link-local ranges remain directly reachable so local gateways and services continue working.

iOS does not offer Android-style per-application allow lists to ordinary unmanaged consumer VPN apps. Website/domain routing remains available through the shared Xray routing configuration.
