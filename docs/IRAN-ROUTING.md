# Iran routing and readable settings (v2.0.23)

The routing page now has a short, 16-point explanation instead of the long mixed
Persian/English Whitelist subtitle. It separates the two routing modes:

- **Iran bypass (default):** known Iranian domains, `.ir` / `.ایران`, and Iranian
  IPv4/IPv6 allocations use the direct outbound. Other traffic uses the proxy.
- **Selected-only mode (legacy):** selected websites/applications use the proxy;
  unmatched traffic uses the direct outbound. This mode is disabled while Iran
  bypass is enabled, and existing users of the legacy mode retain it on upgrade.

Iran bypass includes a compressed snapshot of 57,554 domain matchers, 2 proxy
exceptions and 2,543 IPv4/IPv6 CIDRs. The snapshot comes from:

- [Iran Hosted Domains release 202609280201](https://github.com/bootmortis/iran-hosted-domains/releases/tag/202609280201),
  `qv2ray_schema.json`, SHA256
  `93e5ca20cb4e4db5f71bfa941d319881920d047077c44771d3e4836d9be24cc3`.
- [RIPEstat country resources](https://stat.ripe.net/docs/data-api/api-endpoints/country-resource-list),
  IR IPv4/IPv6 allocation data for 2026-10-02.

The embedded compressed file is approximately 285 KiB and is loaded once off
the UI thread. The default domain list is not rendered in the settings page.
The original MIT license is included in the assembly and Windows package.
`scripts/update-iran-routing.ps1` reproduces the pinned snapshot; update its
release tag and expected checksum deliberately before regenerating domains.
List refreshes are distributed with application updates, not silently fetched
during connection startup.

User lists can be pasted or imported from text files. URLs are reduced to host
names, Unicode names become IDNA names, duplicates are removed and CIDRs are
canonicalized. Invalid lines are counted rather than becoming routing rules.
Imports are bounded to 4 MiB of text / 100,000 entries; only the first 20 personal
entries are displayed. Personal entries are saved separately from the bundled
snapshot. Users can remove individual preview entries or clear their personal
list without deleting the default list.

Routing order is: proxy connection probes, private networks, encrypted routing
DNS through the proxy, personal direct entries, upstream proxy exceptions,
Iranian domains, then Iranian IP ranges. The default outbound remains proxy.
DNS-based IP classification uses cached HTTPS resolvers through the proxy,
instead of relying on an ISP block-page IP. Full preconnection tests never use
split routing. Android's per-app allowed list is not applied in Iran mode, so
unselected apps do not accidentally bypass foreign traffic. Windows preserves
and restores the previous system proxy state, while excluding inherited foreign
bypass patterns during Iran routing.

Limitations: no domain/geolocation list can be guaranteed complete. `.ir` is an
Iranian-service classification, not proof of physical hosting. Country IP
allocations can differ from current deployment geography. Windows still uses
the system proxy backend, so only applications honoring that proxy are covered.
Changes to routing are made while disconnected and take effect on the next
connection.

Verification: all 83 Core tests pass, including all three platform config
builders, rule precedence, encrypted DNS, imported-list validation, disabling
the policy and preserving proxy-only connection tests. Xray 26.7.28 accepts the
complete Windows configuration with `run -test` (no live connection was made).
On the development machine, config generation measured 138 ms cold and 13.8 ms
averaged over 10 cached builds; these timings are not mobile benchmarks.
