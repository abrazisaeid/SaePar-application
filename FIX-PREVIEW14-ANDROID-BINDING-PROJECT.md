# Preview 14 — Android binding project correction

Preview 13 put `SaeParXrayBridge.aar` directly in the MAUI application project.
In an application project, `AndroidLibrary` is used as an Android library/package
input; reliable managed AAR binding is performed in an Android Java Binding Library.

Preview 14 adds:

- `src/SaeParTunnel.AndroidBinding/SaeParTunnel.AndroidBinding.csproj`
- `SaeParXrayBridge.aar` with managed binding enabled (default)
- upstream `libXray.aar` with `Bind="false"` so it is packaged as the bridge dependency
- a conditional ProjectReference from `SaeParTunnel.App` for the Android target

The generated managed namespace is consumed by the existing Android code as:

`Com.Saepar.Tunnel.Bridge.SaeParXrayBridge`

Do not add the AARs directly back to the MAUI app project.
