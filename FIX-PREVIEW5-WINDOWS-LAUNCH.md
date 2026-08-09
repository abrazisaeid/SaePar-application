# Preview 5 — Windows MSIX debug profile fix

Preview 4 was missing `src/SaeParTunnel.App/Properties/launchSettings.json`.
For packaged .NET MAUI Windows debugging, Visual Studio requires a launch
profile whose `commandName` is `MsixPackage`.

The project now includes:

```json
{
  "$schema": "http://json.schemastore.org/launchsettings.json",
  "profiles": {
    "Windows Machine": {
      "commandName": "MsixPackage",
      "nativeDebugging": false
    }
  }
}
```

## Run on Windows

1. Open `SaeParTunnel.CrossPlatform.sln`.
2. Set `SaeParTunnel.App` as Startup Project.
3. Select `Windows Machine` / the `net9.0-windows10.0.19041.0` target.
4. Enable Windows Developer Mode if Visual Studio asks for package deployment permission.
5. Clean, rebuild, then press F5.
