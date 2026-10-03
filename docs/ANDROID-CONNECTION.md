# Android connection recovery (2.0.24)

The standalone server test starts a SOCKS proxy, whereas connecting starts the
Android VPN service and a TUN interface. A successful server test does not prove
that local VPN startup will succeed.

The Iran routing snapshot makes the production configuration roughly 2.1 MB.
Version 2.0.23 passed that entire JSON string in an Intent extra. Android Binder
has a shared 1 MB transaction buffer, so this path can fail before the VPN
service receives the configuration:
https://developer.android.com/reference/android/os/TransactionTooLargeException

The service now receives only a 32-character token. It reads and deletes the
matching configuration from an app-private cache directory off the main thread.
The token is validated as a GUID; incoming arbitrary paths are never accepted.
Caller cleanup also covers service startup failure and timeout.

Connection attempts no longer mark tested servers as failed or increase the
failure counter used for old-server deletion. Standalone server retests still
update health normally. Local VPN startup errors retain the candidates and show
the failure instead of requesting another search.

Retries await complete destruction/cleanup of the previous service. Sending a
stop command or reporting a startup error is not treated as a shutdown
acknowledgement. Duplicate stop requests are suppressed; native final cleanup
runs off Android's main thread to avoid waiting for the bridge lock in OnDestroy.

The Android interface MTU matches the Xray TUN configuration (1400). The native
core's protected bootstrap resolver uses the physical network's DNS captured
before establishing the VPN. HTTP connectivity checks require HTTP 204.

Regression tests cover transferring the actual production Iran routing JSON,
path rejection, cancelled-write cleanup, and preserving five previously tested
servers after VPN connection failure. Compilation and package verification do
not establish successful connectivity on a user's mobile network.
