# Validation — Preview 13

Static checks performed in the generation environment:

- Project/Application version updated to 2.0.13 / 32.
- All XML, XAML and csproj files parse as XML.
- Modified Android/shared C# files passed lexical bracket-balance checks.
- Official libXray Android AAR is bundled and contains `classes.jar` plus
  `libgojni.so` for arm64-v8a, armeabi-v7a, x86 and x86_64.
- Bundled AAR SHA-256 is
  `4708a361a74f7e955635dbe3661cefb459bdc867423c3b1826a2c5a6ea4ac77d`.
- `SaeParXrayBridge.java` was compiled with `javac` against the actual upstream
  libXray `classes.jar` (using minimal Android API stubs) to verify Java syntax
  and the `DialerController`/`LibXray` interface calls.
- Android manifest declares VPN foreground-service permissions, BIND_VPN_SERVICE,
  VpnService intent filter, Android 14+ specialUse foreground-service type/permission, runtime foreground type, and an explicit Always-on VPN opt-out.
- Android real-proxy test request uses libXray API v1 `ping` and a temporary
  loopback SOCKS inbound.
- Android connection uses libXray API v1 `runXrayFromJson` and the Xray TUN
  inbound with a VpnService-owned descriptor.

Limitation of this validation environment:

- The container does not have `dotnet` or the .NET Android/MAUI workload, so an
  APK/AAB could not be compiled here. Build and runtime validation must be done
  in Visual Studio 2022 on the user's Android device. The first device run is
  therefore the integration validation for generated Java binding names,
  manifest merge and vendor-specific VPN behavior.

## Preview 20 Android TUN fd validation
- Rebuilt `SaeParXrayBridge.aar` from updated Java source against the actual bundled libXray `classes.jar` using minimal Android API stubs.
- Verified the AAR exports `initialize`, `attachTun`, `detachTun`, `invoke`, and compatibility `getStableTunFd` methods.
- Android service injects the live `ParcelFileDescriptor.Fd` into Xray root JSON `env["xray.tun.fd"]` immediately before `runXrayFromJson`.
- XML/XAML/csproj parse validation passed.
- Full .NET MAUI Android compilation still requires the user's Visual Studio/.NET Android workload; this container has no `dotnet` SDK.
