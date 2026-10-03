# Release Process

GitHub Releases can contain these downloadable assets:

- `SaeParTunnel-<version>-android.apk`
- `SaeParTunnel-<version>-windows-x64.zip`
- `SaeParTunnel-<version>-windows-x64.exe`
- `SaeParTunnel-<version>-ios.ipa` when Apple signing is configured
- `SHA256SUMS.txt`

The Windows ZIP is a self-contained x64 application. Extract the entire archive
and run `SaeParTunnel.App.exe`; keep the DLLs and other files beside the EXE.
The package includes the verified official Xray v26.7.28 runtime and its license,
so the first search does not require a separate engine download. Android APKs
support Android 7.0+ on arm64 and x86_64 devices.

The installation EXE contains the complete verified ZIP and its own .NET runtime.
It installs into `%LOCALAPPDATA%\Programs\SaeParTunnel\<version>` and creates
Desktop and Start menu shortcuts without administrator permissions. Each version
has its own folder; user profiles/settings are stored separately. To remove a
version, close the app and remove its version folder and shortcuts. Build this
EXE separately after ZIP packaging with `scripts/package-windows-exe.ps1`.

Do not commit release binaries, certificates, provisioning profiles, keystores or passwords to the repository.

## Android and Windows local packaging

Create signed Android and Windows assets with a local Android signing key:

```powershell
.\scripts\package-release.ps1 -GenerateLocalAndroidKeyStore
```

Assets are written to `artifacts\release\v<version>\`. The generated keystore and password stay outside the repository:

```text
%USERPROFILE%\.saepar-tunnel\android-release.keystore
%USERPROFILE%\.saepar-tunnel\android-release-password.txt
```

Back up both files. Every Android update must be signed with the same key.

## iOS local packaging

An installable IPA must be built on macOS with Xcode and Apple signing material. Create two explicit App IDs and provisioning profiles with the Network Extensions / Packet Tunnel capability:

```text
com.saepar.tunnel
com.saepar.tunnel.packet-tunnel
```

Install the certificate and profiles on the Mac, then run:

```bash
export IOS_SIGNING_KEY='Apple Distribution: Example Company (TEAMID)'
export IOS_APP_PROVISIONING_PROFILE='APP_PROFILE_UUID'
export IOS_EXTENSION_PROVISIONING_PROFILE='EXTENSION_PROFILE_UUID'
./scripts/package-ios.sh v2.0.22
```

Set `IOS_SIGNING_KEYCHAIN` when the certificate is in a custom keychain. The script restores the pinned libXray framework, builds for `ios-arm64`, creates the IPA and verifies its archive, bundle IDs, Packet Tunnel extension, embedded profiles and code signature.

For a directly downloadable GitHub IPA, use Ad Hoc profiles. It will only install on devices whose UDIDs are included in those profiles. App Store profiles are intended for App Store Connect/TestFlight and do not make a GitHub IPA generally sideloadable.

## Release quality gate

Before publishing assets:

- Run `dotnet test tests\SaeParTunnel.Core.Tests\SaeParTunnel.Core.Tests.csproj -m:1`.
- Build Windows for `net9.0-windows10.0.19041.0`.
- Build Android for `net9.0-android`.
- Build the iOS app and Packet Tunnel extension on macOS for an iOS simulator.
- On a physical iPhone, approve VPN permission and verify the UI remains disconnected until the tunnel internet test succeeds.
- Confirm public web traffic uses the selected profile while local IPv4/IPv6 services remain reachable.
- Verify Connect becomes disabled and Disconnect becomes prominent only after validation succeeds.
- Run the simple search and confirm it stops after finding five validated healthy profiles. Check cancellation and the advanced full-test controls separately.
- Confirm Dashboard, Configs, Settings and Diagnostics show one consistent connection state.

## GitHub secrets

Android and Windows release jobs require:

```text
ANDROID_KEYSTORE_BASE64
ANDROID_KEY_ALIAS
ANDROID_KEYSTORE_PASSWORD
ANDROID_KEY_PASSWORD
```

Create the Android keystore secret value on Windows:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("$env:USERPROFILE\.saepar-tunnel\android-release.keystore")) | Set-Clipboard
```

Signed iOS packaging is enabled when all four Apple secrets exist:

```text
IOS_DISTRIBUTION_CERTIFICATE_BASE64
IOS_DISTRIBUTION_CERTIFICATE_PASSWORD
IOS_APP_PROVISIONING_PROFILE_BASE64
IOS_EXTENSION_PROVISIONING_PROFILE_BASE64
```

Generate each base64 value on macOS without line breaks:

```bash
base64 < distribution.p12 | tr -d '\n' | pbcopy
base64 < SaeParApp.mobileprovision | tr -d '\n' | pbcopy
base64 < SaeParPacketTunnel.mobileprovision | tr -d '\n' | pbcopy
```

The workflow checks both bundle IDs, the Packet Tunnel entitlement and the Apple team before signing. If none of the Apple secrets exist, the simulator quality gate still runs and the release is published without an IPA. A partially configured Apple signing set fails the release instead of silently publishing an incomplete package.

## Publish a release

Update the app and extension versions first, then create and push the matching tag:

```powershell
git tag -a v2.0.22 -m "SaePar Tunnel v2.0.22"
git push origin main
git push origin v2.0.22
```

The `Release` workflow builds Android and Windows on a Windows runner, validates iOS on a macOS runner, optionally creates the signed IPA, regenerates one checksum manifest and then creates or updates the GitHub Release.
