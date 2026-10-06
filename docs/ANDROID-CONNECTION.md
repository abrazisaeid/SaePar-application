# Android connection recovery (2.0.24)

## Responsive home and fast restoration (2.0.27)

The connected phone was running a debuggable 2.0.26 package with 17,444 archived
profiles (roughly 27 MB). Home displayed saved servers but a global busy flag
also disabled their selection. Advanced/background scans could keep that flag
set for a long time; their stop button was below the server rows.

Server selection now has its own availability rule and stays enabled during
background scans. Home shows progress/Stop before the rows. Connect can cancel
an ongoing scan, await its completion (up to 20 seconds), restore the explicitly
chosen server, and then connect; production/probe cores never deliberately overlap.
The home spinner reports home work rather than every advanced operation.

Atomic `home-servers.json` snapshots contain at most five saved profiles. Settings
and this small snapshot use independent IO gates from the full archive, allowing
home rows to appear/select while archive restoration continues. Import/search/
connection waits until archive restoration finishes to avoid saving a partial list
over the full archive. Startup shares one initialization task across page visits.
The bundled Iran catalog summary is loaded separately. Cached compatibility checks
inspect stored fields, without parsing and hashing every URI a second time. Legacy
backup history is inspected on the initial upgrade; subsequent restores merge the
small home history instead of deserializing another whole archive. Hidden advanced
lists are not sorted in quick mode.

Managed cancellation does not interrupt JNI. Native probes now have bounded waits
(8 seconds fast, 12 normal), while the native operation retains its gate and temp
file until it really returns. Late faults are observed. A timeout is reported as a
local engine failure and stops testing rather than marking that server dead. A new
VPN start waits for actual native idleness (up to 8 seconds). If native work itself
never returns, the process still needs restarting; the UI no longer waits forever.

Validation: 128 tests, including fast snapshot reads while the archive is locked,
third-server recovery, cleanup reflected in snapshots, optional-cache error
isolation, and cancelled/timed-out native simulations preserving serialization.
These simulations do not establish VPN connectivity on a mobile network.

## Saved home servers and individual sharing (2.0.26)

The home picker previously filtered exclusively on the latest `Working` status.
A failed standalone retest could therefore hide all previously discovered
servers on restart, even though their records remained in `profiles.json`.
Profiles now retain the last successful full-proxy test and its latency separately
from the latest result. Home shows up to five saved servers, permits another
connection attempt, and labels failed retests and previous latency explicitly.
Current healthy totals still count only the latest successful results, and
connection still requires actual full-proxy validation before showing connected.
Normal old-server cleanup continues to remove eligible failed entries.

Legacy working profiles are migrated when loaded. For existing failed records,
an available backup can restore prior successful-test history for matching IDs;
it never resurrects removed records or overrides the latest failure. Missing
historical information cannot be reconstructed if no valid backup contains it.

The selected server ID is persisted. Home lists individual numbered rows with
selection, latency, last-test information and an independent Share action for
that exact profile (text, clipboard or QR), including while connected.

Startup restores profile data independently from settings. A settings read error
does not suppress profile restoration; initialization remains retryable after a
load error. A cache whose read failed cannot be overwritten with defaults until
it is successfully read again, preserving primary and backup files for recovery.

Validation: 119 regression tests passed, including the actual application store
with only the platform directory provider substituted, migration, backup history,
no deleted-server resurrection, five retained failed-retest records after restart,
third-server selection and unreadable-settings write protection. Native device
UI, sharing and mobile-network VPN connectivity still need device verification.

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
