# Validation

Validation status for SaePar Tunnel 2.0.21:

## Automated checks

- Core test suite: 23 passed, 0 failed.
- iOS binding Release build for `iossimulator-x64`: passed with 0 errors.
- Packet Tunnel extension Release build for `iossimulator-x64`: passed with 0 errors.
- Full MAUI iOS Release build for `iossimulator-x64`: passed with 0 errors.
- Full MAUI iOS Release publish for `ios-arm64` with code signing disabled: passed with 0 errors.
- Android Debug build: passed with 0 errors.
- Windows Debug `win-x64` build: passed with 0 errors.
- App and extension signing-property evaluation confirms separate provisioning profiles and a shared identity/keychain.
- All iOS shell scripts pass `bash -n` syntax validation.
- GitHub Actions workflow files parse as YAML and all referenced official action major tags exist.
- Modified files pass `git diff --check`.

The existing MAUI/XAML and network vulnerability-feed warnings remain non-fatal. The vulnerability-feed warning occurs when `api.nuget.org` is unavailable and does not indicate a discovered vulnerable package.

## Native artifact

The iOS build restores the official XTLS/libXray `v26.7.28` Apple XCFramework. `fetch-libxray.sh` pins and checks the archive with SHA-256:

```text
07f7ed7697277930e1c517755855950f594f41435b0dfc5917a66eea6278aeb9
```

The XCFramework contains device and simulator slices used by the successful local compile checks.

## Physical-device and distribution gates

These checks require macOS, Xcode, an Apple Developer team and a physical iPhone:

- Sign the host app and Packet Tunnel extension with separate matching provisioning profiles.
- Run `scripts/package-ios.sh` and verify the resulting IPA passes its embedded signature/entitlement checks.
- Approve the system VPN prompt and connect on iOS 15 or later.
- Confirm the UI does not report a successful connection before its internet validation passes.
- Confirm failed validation disconnects the tunnel.
- Confirm public IPv4/IPv6 traffic uses Xray while private and link-local LAN services remain reachable.
- Exercise sleep/wake, network changes, reconnect, disconnect and app relaunch.

An Ad Hoc IPA can only be installed on registered devices. General user distribution must use TestFlight/App Store or another Apple-approved distribution method.
