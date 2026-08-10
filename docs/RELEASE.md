# Release Process

This project publishes downloadable assets through GitHub Releases:

- `SaeParTunnel-<version>-android.apk`
- `SaeParTunnel-<version>-windows-x64.zip`
- `SHA256SUMS.txt`

Do not commit release binaries or Android signing keys to the repository.

## Local Packaging

To create local release assets with a local Android signing key:

```powershell
.\scripts\package-release.ps1 -GenerateLocalAndroidKeyStore
```

The script writes assets to:

```text
artifacts\release\v<version>\
```

When `-GenerateLocalAndroidKeyStore` is used, the keystore and password are stored outside the repository:

```text
%USERPROFILE%\.saepar-tunnel\android-release.keystore
%USERPROFILE%\.saepar-tunnel\android-release-password.txt
```

Back up both files. Android app updates must be signed with the same key.

## Release Quality Gate

Before publishing the downloadable assets, run these checks:

- `dotnet test tests\SaeParTunnel.Core.Tests\SaeParTunnel.Core.Tests.csproj -m:1`
- Windows debug or release build for `net9.0-windows10.0.19041.0`
- Android debug or release build for `net9.0-android`
- Open the app and verify the main screen shows `Disconnected` until the tunnel internet test passes.
- In Configs, run guided health testing and confirm the 5-healthy checkpoint prompt appears before continuing.
- Confirm the best profile card, Connect, Disconnect, Settings and Diagnostics pages all reflect the same connection state.
- Copy the Diagnostics report once and attach it to the release notes only when troubleshooting context is needed.

## GitHub Release Workflow

Before pushing a release tag, add these repository secrets:

```text
ANDROID_KEYSTORE_BASE64
ANDROID_KEY_ALIAS
ANDROID_KEYSTORE_PASSWORD
ANDROID_KEY_PASSWORD
```

To convert the local keystore to a GitHub secret value:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("$env:USERPROFILE\.saepar-tunnel\android-release.keystore")) | Set-Clipboard
```

Create and publish a release:

```powershell
git tag -a v2.0.19 -m "SaePar Tunnel v2.0.19"
git push origin main
git push origin v2.0.19
```

The `Release` workflow builds the APK and Windows ZIP, then uploads them to the GitHub Release for that tag.
