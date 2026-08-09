# Preview 11 — Windows port recovery + config sorting + multi-test

## Windows connect error fixed
The Preview 10 error `failed to listen TCP on 10809` was a local port collision, not an `xray.exe` path problem.

Preview 11:
- checks the requested SOCKS/HTTP ports before starting Xray;
- automatically switches to a free local port pair if 10808/10809 (or configured ports) are busy;
- retries with a new pair when Xray explicitly reports a bind/listen conflict;
- does not kill unrelated `xray.exe` processes that may belong to another application;
- updates Windows System Proxy to the actual HTTP port selected for the successful connection.

The old dedicated current-connection Probe inbound/UI has been removed. A server is tested before connection using the same Full-Proxy test flow used by the healthy-server picker and Configs page.

## Configs page
- Added sort picker with: newest added, oldest added, lowest ping, highest ping, newest tested, name.
- Default sort is newest added.
- Each config card shows its FirstSeen timestamp.
- Each config card has a checkbox for multi-selection.
- `تست موارد تیک‌خورده` tests all checked profiles.
- Added `انتخاب موارد نمایش‌داده‌شده` and `پاک کردن انتخاب` helpers.

## Dashboard
The separate `اتصال فعلی / Ping-Test` panel was removed. The healthy-server picker still shows only Full-Test-working profiles, displays ping, and supports retesting the selected candidate before Connect.
