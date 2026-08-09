# Preview 17 — Android data-plane validation

Preview 16 reported Connected once libXray accepted the Xray configuration. That proved control-plane startup, but not that packets from Android actually traversed TUN -> Xray -> Internet.

Preview 17 changes Android connection semantics:

- Android MTU and Xray TUN MTU are both 1400.
- In full-tunnel mode SaePar itself is no longer excluded from the VPN. Only Xray upstream sockets are excluded via the official libXray DialerController -> VpnService.protect(fd) path.
- In Android app-whitelist mode SaePar itself is included automatically so the app can validate the same TUN path.
- After Xray starts, SaePar performs a real HTTP request from its own UID. `Connected` is emitted only after that request succeeds through the Android VPN.
- If Xray starts but packets do not traverse the tunnel, the UI now reports a data-plane error instead of a false Connected state.
- Xray TUN log level is `info` for Android diagnostics.
