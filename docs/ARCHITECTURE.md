# Architecture

```
SaeParTunnel.Core (net10.0)
  Models / ConfigParser / ConfigExtractor / GitHub / XrayConfigBuilder / endpoint precheck
           ↑
SaeParTunnel.App (.NET MAUI)
  shared Pages + MainViewModel + JSON storage
           ↓
  ITunnelService
     ├─ Windows: Xray process + System Proxy (working backend)
     ├─ Android: VpnService + libXray bridge (native integration slot)
     └─ iOS: NetworkExtension + libXray bridge (native integration slot)
```

Windows reuses `%LOCALAPPDATA%/SaeParTunnel`, so v1.x profiles/settings migrate automatically.
Android and iOS use MAUI's app-data directory.
