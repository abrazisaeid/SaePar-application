# Android connection recovery (2.0.24)

## GeoIP and cache persistence correction (2.0.25)

The production routing builder previously used `geoip:private`, although the
Android package does not ship or extract `geoip.dat`. The small standalone
server probe has no routing rules, so it could pass while connecting failed with
`illegal ip rule: geoip:private` and `failed to open geoip.dat`.
The builder now embeds the equivalent 18 explicit IPv4/IPv6 CIDRs from:
https://raw.githubusercontent.com/Loyalsoldier/geoip/release/text/private.txt
All generated production modes are independent of external GeoIP/GeoSite files.

The exact old error was reproduced with Xray 26.7.28 in an empty asset directory.
The corrected Windows configuration and Android routing configuration (with a
SOCKS inbound for the desktop-only parser check) both returned Configuration OK
with no geodata assets. This validates routing parsing, not Android TUN traffic.

Healthy results are checkpointed during discovery instead of waiting only for
the end of a scan. A cached working profile retains that state during a retest
until a new result arrives. Persistent settings/profile metadata now uses a
source-generated JSON contract; file writes flush to disk before rename and keep
a backup. Reads recover a complete interrupted first write or a valid backup,
and report an error instead of silently replacing unreadable caches with an
empty list. Scan UI state is also released if final persistence fails.

Validation: 108 regression tests passed, including five healthy profiles and
their latency after restarting the store, backup recovery, interrupted first
write recovery, unreadable-cache preservation, and six platform/mode checks for
asset-free routing. Actual mobile-network VPN connectivity remains unverified.

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
