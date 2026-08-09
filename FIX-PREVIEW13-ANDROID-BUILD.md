# Preview 13 — Android build integration fix

Preview 12 correctly bundled the upstream libXray AAR but made one .NET-for-Android integration mistake: a Java source file compiled as `AndroidJavaSource` does not automatically create a managed C# namespace. The C# files referenced `Com.Saepar.Tunnel.Bridge.SaeParXrayBridge`, so Visual Studio reported CS0246/CS0103.

Preview 13 packages the already validated Java shim as `SaeParXrayBridge.aar` and binds that tiny AAR (`Bind=true`). The large upstream `libXray.aar` remains `Bind=false`; it is consumed only by the shim.

Also fixed:
- ambiguous `OperationCanceledException` by explicitly using `System.OperationCanceledException`;
- namespace collision for `Android.Manifest` inside `SaeParTunnel.App.Platforms.Android` by using `global::Android.Manifest`;
- fully-qualified Android package/resource references in the VPN service.

The Java bridge AAR was compiled against the actual `libXray.aar` `classes.jar` and its public surface is:
- `initialize()`
- `attachTun(VpnService, ParcelFileDescriptor, String)`
- `detachTun()`
- `invoke(String)`
- `getStableTunFd()`
