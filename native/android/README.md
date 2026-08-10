# Android native tunnel bridge

The MAUI app and shared core already build/run on Android. The remaining native VPN step is intentionally isolated here.

Target architecture:
1. Build the official XTLS/libXray Android artifact with its supported build script.
2. Bind the resulting AAR to .NET for Android.
3. Add an Android `VpnService` that obtains the TUN fd.
4. Put `xray.tun.fd` in the root Xray config `env` object before invoking libXray.
5. Apply `VpnService.Builder.AddAllowedApplication(packageName)` for the application whitelist.

Build restore:
- `SaeParTunnel.AndroidBinding` restores the official XTLS/libXray `v26.7.28`
  Android release asset automatically when `Libraries/libXray.aar` is missing.
- The downloaded `libxray-android.zip` and extracted `libXray.aar` are verified
  with SHA-256 before the binding project continues.
- `Libraries/libXray.aar` remains git-ignored because it is a large generated
  runtime artifact; the project file is the source of truth for restoring it.

Do not commit private signing keys or user VPN state.
