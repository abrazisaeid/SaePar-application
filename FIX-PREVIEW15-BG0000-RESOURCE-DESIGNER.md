# Preview 15 — Android binding BG0000 fix

Preview 14 could fail inside the .NET for Android Java binding generator with:

- `BG0000: System.NullReferenceException`
- missing `_Microsoft.Android.Resource.Designer.dll`

The Android binding library does not contain managed Android resources, but .NET 9
enables the resource designer assembly path by default. Preview 15 disables both
`AndroidGenerateResourceDesigner` and `AndroidUseDesignerAssembly` for the resource-free
binding library. It also removes the obsolete/legacy `IsBindingProject` property and
disables default Android item globbing so the two AARs are included exactly once with
the intended `Bind` metadata.

Before rebuilding, close Visual Studio and remove `%LOCALAPPDATA%\SPTBuild` to discard
Preview 14 design-time/build artifacts.
