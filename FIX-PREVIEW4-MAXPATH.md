# Preview 4 - Android XA5301 / MAX_PATH fix

The Android error `XA5301 ... due to MAX_PATH` is caused by generated Java wrapper paths becoming longer than the Windows path limit used by parts of the Android build toolchain.

Preview 4 changes the Windows build layout:

- generated `obj` files -> `%LOCALAPPDATA%\SPTBuild\obj\<ProjectName>\...`
- generated `bin` files -> `%LOCALAPPDATA%\SPTBuild\bin\<ProjectName>\...`

This keeps the generated Android path substantially shorter than putting `obj` under the source directory.

## Recommended first build

1. Close Visual Studio.
2. Extract this ZIP to a short folder such as `C:\SPT`.
3. Open `SaeParTunnel.CrossPlatform.sln`.
4. Set `SaeParTunnel.App` as Startup Project.
5. Select the Android emulator/device or Windows Machine as required.
6. Run **Build > Clean Solution**, then **Build > Rebuild Solution**.

## If Preview 3 was already built

Delete the old generated folders before reopening Visual Studio:

```powershell
Remove-Item -Recurse -Force .\src\SaeParTunnel.App\obj -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force .\src\SaeParTunnel.App\bin -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\SPTBuild" -ErrorAction SilentlyContinue
```

Preview 4 also includes `scripts/clean-build.ps1` to do this automatically.
