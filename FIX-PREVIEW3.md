# Preview 3 fix

Fixed CS0103 in `MauiJsonStore.cs` by importing `SaeParTunnel.Core.Services`, where `GitHubConfigService` is declared.

Affected line:

```csharp
using SaeParTunnel.Core.Services;
```

The app package/display version is 2.0.2 for this preview.
