# Android connection recovery (2.0.24)

## Restart discovery while connected (2.0.33)

The Android home screen has a Find replacement server button below Disconnect
whenever the VPN is connected. It starts the same physical-network discovery
without asking the after-connect question again, including when that question
was declined. Discovery fills the list to ten recently verified servers; if ten
are already ready, it reports that they are available without starting more tests.
The existing three-minute limit, per-success persistence, Stop test button and
immediate Disconnect action still apply.

The button is disabled during connection changes, ping, initialization or another
search. Both the manual action and the after-connect prompt recheck availability
and the connection generation before starting, so a delayed prompt cannot start
a second search. Disconnected search and routing-edit guards stay separate.

## Hide unresponsive home servers (2.0.32)

A completed failed probe removes a previously verified configuration from the
home list immediately. Its URI and last successful test remain in the archive
under the Failed filter for retesting and normal cleanup; keeping that history
does not make it eligible for the home list or automatic connection attempts.
Neither starting a retest nor a TCP-only success restores it. A full successful
test restores it and resets consecutive failures.

The failed profile is checkpointed and the fast home cache is updated as soon
as its result arrives, including during longer discovery runs. A restart, stale
selected ID, or legacy history recovery cannot put it back in the home list.
When disconnected, another eligible server becomes selected. Hiding the active
profile does not disconnect its running VPN; after disconnect, selection moves
to another eligible server.

If the connected-server ping fails on Android, the app confirms the server with
the isolated physical-network probe. A failed TUN ping alone therefore does not
demote a server that still passes its independent test. Cancellation and known
network/worker infrastructure failures do not increment its failure count.
Disconnect remains available during ping and cancels it; a result from a connection
that was disconnected or replaced cannot demote its configuration.

Advanced settings show the failed count and a button opening the failed archive.
Permanent cleanup still requires three consecutive failures, seven days of age,
and another recent success; the active profile is protected.

## Optional discovery during a connection (2.0.31)

After a new connection passes actual VPN validation, Android offers to continue
discovery until there are **10 total** recently verified configurations. Declining
starts no discovery. A stale answer after disconnect/reconnect is discarded.
Existing successes from the last 30 minutes count; historical successes, endpoint
checks and duplicate IDs do not. The active configuration is never retested or
replaced. Ten saved configurations now fit in both the home list and fast cache.

The user can stop discovery separately or disconnect immediately from the page
or VPN notification. Disconnect cancels discovery. One scan has a three-minute
budget and retains each success as it arrives. It tries archive candidates first
and updated subscriptions once if the archive is exhausted. It can finish with
fewer than ten if the network or available configurations do not provide ten.

`AndroidDirectNetwork` selects an Internet/NotVpn network for the search. Managed
DNS uses `Network.getAllByName`; each HTTP/precheck socket is bound explicitly
before connecting. The duplicate descriptor is closed without closing the managed
socket. HTTP connection pooling cannot carry subsequent searches to an old network,
and Android downloads only one direct copy rather than racing identical routes.
At most four endpoint checks/DNS lookups run concurrently; native probes remain
serial. A cancelled DNS caller does not release its concurrency slot until the
actual Android lookup finishes.

The private `:probe` process receives the same Parcelable Network. Only that process
uses `bindProcessToNetwork`; the Java dialer controller also explicitly binds Go
socket descriptors, including bootstrap DNS, to the physical network. A failed
binding or vanished search network stops the run without marking configurations
dead. The main application stays on the VPN so connection validation and the
connected-server ping still exercise the real TUN. No app-wide bypass is installed.

`scripts/build-android-bridge.ps1` rebuilds the small checked-in Java shim using
the pinned official libXray AAR. Tests cover topping up five to ten, excluding the
active server, freshness/duplicate counting, transport failure propagation,
loopback stream ownership and restoring all ten saved configurations.

References: [Android Network socket binding and DNS](https://developer.android.com/reference/android/net/Network),
[process binding](https://developer.android.com/reference/android/net/ConnectivityManager#bindProcessToNetwork(android.net.Network)),
and [libXray controller error handling](https://github.com/XTLS/libXray/blob/v26.7.28/controller/controller.go).

## Live VPN notification (2.0.30)

Android's foreground notification shows connection/startup/validation/shutdown
state, the selected server, upload/download rates, and session byte totals in its
expanded view. Rates are sampled every second from the native core's `proxy`
outbound counters; `direct` (including Iran bypass), metrics requests, and blocked
traffic are excluded. This indicates VPN service state, not a continuous proof
that every destination on the internet is reachable.

`BuildAndroidTun` enables outbound statistics and a metrics listener on an
ephemeral `127.0.0.1` port. A managed HTTP client reads `/debug/vars`, with no
proxy/redirects, a two-second timeout, cancellation, and a 256 KB response bound.
No additional native or gRPC library is required. Monotonic elapsed time determines
rates; a missing response shows unavailable statistics, and a counter reset cannot
produce a negative rate. Polling uses no native bridge lock or persistent writes.

The immutable Disconnect action targets the VPN service directly and is scoped
to the current service instance. It cancels pending validation as well as active
traffic monitoring. All publication/removal uses one lock; shutdown cancels and
awaits the polling task before stopping Xray, so stale ticks cannot repost the
notification. App/notification shutdown callers share one destruction
acknowledgement with independent cancellation. User cancellation during startup
does not trigger another server attempt or a timeout error.

Android 13+ requests notification permission on first connection. Denying it
does not prevent VPN use or cause repeated dialogs. Advanced → VPN notifications
opens Android's notification settings to enable it later. The status-bar icon is
a transparent white SP vector, not the system information symbol.

The opt-in Debug property `SaeParNotificationDiagnostics=true` and activity extra
`saepar-notification-selftest` run a local-only device fixture. It transfers known
byte counts over SOCKS to a loopback listener, inspects the actual notification,
sends its Disconnect PendingIntent, and verifies core/service termination and no
notification repost. Its configuration blocks all non-loopback destinations and
creates no TUN. These hooks are absent from Release.

References: [Xray metrics](https://xtls.github.io/en/config/metrics.html),
[statistics](https://xtls.github.io/en/config/stats.html), and
[Android notification permission](https://developer.android.com/develop/ui/views/notifications/notification-permission).

## Launcher icon (2.0.29)

The application manifest now explicitly references `@mipmap/appicon` and
`@mipmap/appicon_round`. Previously the packaged manifest had no icon reference,
so the phone launcher displayed Android's default application icon even though
MAUI had generated the logo resources.

The icon now has a full-bleed solid background and a centered, transparent SP
foreground. Android scales only the foreground to 0.78 for its adaptive masks.
The generated monochrome layer uses that transparent glyph, avoiding the opaque
rounded rectangle that previously concealed the logo in themed icons. Windows
and iOS retain the normal foreground size.

References: [MAUI app icons](https://learn.microsoft.com/en-us/dotnet/maui/user-interface/images/app-icons?tabs=android)
and [Android adaptive icons](https://developer.android.com/develop/ui/views/launch/icon_design_adaptive).

## Transactional archive and isolated probes (2.0.28)

`profiles.db` now stores one complete profile per SQLite row. The first load
validates and recovers legacy JSON/history before committing all rows and a schema
marker in one transaction. Original JSON/tmp/backup files are preserved. Once
migrated, the database is authoritative; stale JSON cannot resurrect cleaned rows.
Unreadable databases/legacy archives block writes rather than replacing data with
defaults. Settings and the five-server home snapshot remain small atomic JSON files.

Successful probe checkpoints and standalone pings update only their profile row.
Imports and cleanup reconcile a snapshot, writing changed rows and removing absent
IDs atomically. Hashes detect unchanged rows without retaining a second full JSON
archive in memory. Late individual probe results cannot recreate deleted profiles.

Android probes use a private, non-exported bound Messenger service in `:probe`.
Only a validated opaque configuration token crosses Binder. The client receives the
worker PID before starting native work, serializes requests, and terminates only
that private process on cancellation, deadline, or service failure. The next probe
binds a fresh worker. VPN startup stops the probe worker; the main VPN core's JNI
lock cannot be held by a hung test. Actual VPN success still requires internet
validation. No failed local engine startup is counted as a dead server.

The opt-in Debug build property `SaeParProbeDiagnostics=true` enables a device
check (`saepar-probe-selftest` activity boolean extra). It uses invalid local Xray
configuration, then a simulated hung worker, cancellation, and a second local
native call. It never tests public proxies. All diagnostic entry points and hang
simulation are absent from Release builds.

Validation: 135 core/app-store tests, including changed-row counts, concurrent
individual pings, transaction rollback/retry, corruption write protection, legacy
file preservation, and no deleted-server resurrection after restart.

Device validation on the connected phone: all 15,551 existing records migrated;
four saved home servers and the selected ID were restored. The Debug-only local
native check passed, a simulated hung probe cancelled in about 2.1 seconds, a fresh
worker responded, and watchdog expiry followed by another worker restart passed.
The main app remained alive and no probe process was left afterward. These checks
do not establish end-to-end VPN connectivity on a mobile network.

Implementation references: [SQLite native bundles](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)
and [Android Messenger bound services](https://developer.android.com/develop/background-work/services/bound-services).

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
