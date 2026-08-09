# Preview 19 — Android data-plane probe

Preview 18 used `http://1.1.1.1/` as a fallback connectivity probe while the Android manifest correctly had `android:usesCleartextTraffic="false"`. Android therefore rejected the diagnostic itself with `Cleartext HTTP traffic ... not permitted`, which could be mistaken for a tunnel failure.

Preview 19 removes the clear-text probe entirely and validates the VPN using HTTPS-only endpoints. It also preserves all probe errors instead of overwriting the first useful failure with the last one.

No clear-text traffic permission was enabled; the application keeps `usesCleartextTraffic=false`.
