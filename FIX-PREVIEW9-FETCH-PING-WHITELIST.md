# Preview 9 fixes

- Get Config now retries the configured source using both the system route and a direct (no-system-proxy) HTTP client.
- The default Epodonios source also has GitHub raw and jsDelivr fallback URLs. TLS certificate validation is NOT disabled.
- Fetch errors now include useful inner-exception details and strip HTML `<br>` fragments.
- Added a dedicated localhost `probe-in` Xray HTTP inbound so the active Windows connection can be manually pinged through the selected proxy, even when whitelist mode sends unmatched traffic direct.
- Dashboard now shows the connected profile and a manual **Ping / Test** button with latency in ms.
- Whitelist applications now have **Browse...** on Windows to select `.exe` files.
- On Android, **انتخاب برنامه** enumerates launchable apps and stores the selected package ID. Android VPN routing remains pending until the native VpnService/libXray backend is integrated.
- Removed a duplicate progress UI update that was present in Preview 8.
