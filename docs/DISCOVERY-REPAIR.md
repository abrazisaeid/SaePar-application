# Config discovery and responsiveness repair

## Observed evidence

- The configured public subscription responded with HTTP 200. A live snapshot
  contained 7,679 parseable links, 5,095 distinct connection identities.
- The local cache contained 6,630 entries: 1,024 failed, 5,509 untested,
  93 unsupported and 4 previously working. These cached states are not proof
  of current connectivity. 519 cached messages reported a two-second TCP timeout.
- The installed Windows engine identifies itself as Xray 26.7.28.
- Before compatibility fixes, 14 of 40 sampled configurations were rejected by
  that engine before connecting. Errors included removed `allowInsecure` and
  public VLESS/Trojan connections without transport encryption. Another sampling
  revealed an invalid `security=false` value.

## Changes

- Subscription requests race system-proxy and direct routes, with a 15-second
  deadline per candidate covering headers and body, a 32 MiB body limit, and
  fallback after empty/invalid subscription bodies. Parsed results are reused.
- ETags are committed after successful import, never reused across mirror URLs,
  and not sent when the profile cache is empty.
- Profile identity includes transport and TLS parameters previously omitted
  (gRPC service, fingerprint, ALPN, mode, etc.). JSON encoding prevents separator
  collisions. Existing cache IDs are recalculated on load. Data schema 27 clears
  old fetch validators once so previously discarded variants can be recovered.
  External community-index producers using profile IDs must regenerate those
  IDs with the updated `ConfigParser.ComputeId`; endpoint-based matches remain.
- Parser compatibility checks identify removed engine features and unknown
  transport/security values before starting network tests. Cached original links
  receive the same compatibility checks. This does not make obsolete links work.
- Guided searches prioritize previously working and fresh untested candidates
  over failed archives, independently of the visible list sort order. Newly found
  working profiles become available during the search.
- Busy operations cannot re-enter through commands. Initialization cannot overlap.
  Test cancellation propagates without counting it as a server failure; progress
  callbacks are awaited, concurrency is bounded, and engine preparation happens
  before the worker batch. Original failure details are retained with friendly text.
- Large JSON serialization/deserialization runs away from the UI thread; writes
  retain the existing serialization gate and atomic replacement.
- Android/iOS skip TCP rejection for mKCP and respect disabling fast prechecks.
- Windows validation requires the expected endpoint response instead of treating
  redirects/403/404 responses as working. Its response body has a deadline.
  Engine downloads and community-index bodies also have deadlines. Process logs
  retain only a bounded tail rather than growing for the life of the connection.

## Simple interface, ping and search follow-up

- The main page contains server discovery, selection, ping and connect/disconnect.
  Server sources, diagnostics and routing settings are under the Advanced tab.
  Main-page and settings bindings use compiled XAML bindings.
- Main-page search stops after finding at least five working servers. Each
  invocation considers supported candidates until the target or its two-minute
  cancellation budget. Cancellation already executing inside native libXray
  still depends on its native timeout. Advanced full-list testing remains available.
- Searches use rolling workers instead of batches waiting for their slowest
  member. In-flight tests are canceled when the target is reached, and canceled
  candidates remain available for a later search phase.
- TCP checks of the same address/port share one task within a search phase,
  with a 15-second expiry and bounded cache size. Manual ping uses a fresh check.
  Different endpoints are prioritized before variants of the same endpoint.
  TCP-reachable endpoints precede unknown ones. Connection methods with successful
  local tests in the last 24 hours are prioritized within the same health category.
- Repeated main-page searches reuse profiles when all enabled sources were
  fetched in the last ten minutes; explicit advanced subscription refresh remains.
  A set of five locally working profiles tested within the last 30 minutes also
  allows immediate testing of the cache without waiting for a source download.
  When fetching is needed, at most two enabled sources are fetched concurrently.
- Windows full-proxy tests retain a bounded process pool: eight concurrent
  engines on machines reporting at least eight logical CPUs and 8 GiB available
  GC memory budget; otherwise the existing six-engine limit remains. The setting
  for test workers also covers cheap TCP checks and does not spawn that many engines.
- Guided search no longer displays an ETA for testing the entire archive when
  its actual goal is finding a few working servers. Full-list testing keeps its ETA.
- The ping button checks the selected profile while disconnected, or the active
  connection while connected. It displays a fresh latency or a timeout message.
  Android now performs a real HTTPS check for active ping. Probe domains are
  explicitly routed through the selected proxy when whitelisting is enabled.
- Cleanup requires an entry at least seven days old, three consecutive failures,
  failed health and a working test somewhere in the list in the last 15 minutes.
  The active profile is protected. Removed IDs are suppressed from subscription
  import for seven days. Expired suppression clears validators for a fresh retry.
  Advanced settings provide a toggle and a manual cleanup button.
- These changes do not establish a measured end-to-end discovery speedup on
  the user's network. Deterministic tests verify scheduling, cancellation,
  endpoint reuse, routing and conservative cleanup behavior.

## Validation and limits

- `dotnet test tests/SaeParTunnel.Core.Tests --no-restore`: 61 passed.
- Windows and Android Debug builds completed successfully, with existing build
  warnings (including uncompiled XAML bindings).
- The running Visual Studio/app instance locked the normal Windows output DLL.
  Follow-up Windows builds use `C:/Users/vebko/AppData/Local/SPTUIReview/windows/`
  to verify compilation without stopping the user's running app.
- A separate live-subscription/local-engine check accepted all 40 sampled
  supported configurations using `xray run -test`, after the compatibility fixes.
  This is schema validation, not end-to-end internet validation.
- Automatic approval review blocked a proposed live public-proxy connectivity
  test with “blocked by policy”; no more specific reason was provided.
- iOS device builds and Android/iOS device responsiveness were not verified here.
  Native libXray calls already in progress still rely on the native timeout;
  canceling their managed task cannot forcibly terminate native execution.
- Existing user profiles/settings were inspected but were not overwritten by
  the diagnostic harness. Migration runs when the updated application starts.

Engine compatibility references:
[outbound transport requirements](https://github.com/XTLS/Xray-core/blob/v26.7.28/infra/conf/xray.go),
[TLS removed options](https://github.com/XTLS/Xray-core/blob/v26.7.28/infra/conf/transport_security.go),
[private address exceptions](https://github.com/XTLS/Xray-core/blob/v26.7.28/common/geodata/consts.go).
