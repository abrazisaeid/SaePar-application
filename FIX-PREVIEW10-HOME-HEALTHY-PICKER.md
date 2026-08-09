# Preview 10 — Home healthy-config picker + connect diagnostics

- Home page now has a Picker containing only `ProfileHealth.Working` (Full-Test healthy) configs.
- Items are sorted by latency and display latency, protocol, name and endpoint.
- Selecting an item makes it the active connection candidate.
- `تست مجدد این کانفیگ` retests the selected healthy config before connection. If it fails, it is removed from the healthy Picker automatically.
- Connect can switch between healthy configs; Windows backend already disconnects the old Xray process before connecting the new one.
- `اتصال به بهترین Ping` selects and connects the lowest-latency healthy config.
- Windows Xray auto-download now retries with system proxy and then direct/no-proxy.
- Settings > Windows includes Browse for a local `xray.exe`, so TLS/proxy download failures do not block connection.
- Exception text is flattened and `<br>` artifacts are removed.
