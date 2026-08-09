# Preview 8 — Android responsiveness and feedback

Preview 7 could become unresponsive on a phone after loading thousands of profiles because the UI collection was populated with every matching profile and bulk-test progress generated too many UI updates.

Preview 8 changes that behavior:

- Mobile config list is paged: 120 profiles are rendered initially; more are loaded on demand.
- Windows keeps a larger 1000-row page for desktop use.
- Range updates replace thousands of per-item ObservableCollection notifications.
- GitHub parsing is moved off the UI thread.
- Bulk-test progress refresh is throttled to roughly five UI updates per second.
- The Configs page now shows StatusMessage, ActivityIndicator, test progress, ETA, speed, and a visible Cancel button.
- Errors are shown in an alert instead of being visible only on the Dashboard status label.
- JSON profile storage is compact (not indented) to reduce mobile file size and write time.
- Filtered testing still tests the complete filtered result set, not only the currently rendered page.

## Important Android tunnel status

This preview fixes UI responsiveness and feedback, but it does **not** pretend that the Android VPN backend is complete. The current Android ITunnelService performs endpoint checks only. A real connection requires Android VpnService + the official libXray AAR/native bridge. The Dashboard now shows this limitation explicitly and pressing Connect displays a clear message instead of appearing to do nothing.
