# Preview 6 - Windows debug profile fix

Preview 5 used an MSIX (`MsixPackage`) launch profile. On some Visual Studio 2022 / MAUI setups this produced:

`The project doesn't know how to run the profile with name Windows Machine and command MsixPackage.`

Preview 6 uses the official unpackaged development configuration instead:

```xml
<WindowsPackageType>None</WindowsPackageType>
```

and:

```json
{
  "profiles": {
    "Windows Machine": {
      "commandName": "Project",
      "nativeDebugging": false
    }
  }
}
```

This is intended for local Windows development/debugging. MSIX packaging can be re-enabled for release distribution later.

After extracting Preview 6:

1. Close Visual Studio.
2. Delete `.vs` beside the solution if copied from an older preview.
3. Open `SaeParTunnel.CrossPlatform.sln`.
4. Set `SaeParTunnel.App` as Startup Project.
5. Select the `net9.0-windows...` / Windows Machine target.
6. Clean and Rebuild.
7. Press F5.
