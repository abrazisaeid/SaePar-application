# Preview 18 — Android DNS/startup reliability

Preview 17 could exceed the 25-second UI watchdog because its post-connect HTTP verification was synchronous and Android DNS resolution can outlive HttpClient's nominal request timeout on filtered networks.

Preview 18 changes:

- Captures the physical Wi-Fi/mobile DNS servers before VpnService establishes the tunnel.
- Passes the physical DNS endpoint to libXray.SetDNS so Xray server hostname resolution uses a protected socket outside the VPN, as recommended by libXray's Android DNS integration.
- Sends Android system DNS traffic (TCP/UDP port 53) to the `direct` outbound; libXray's DialerController protects those sockets from the VPN loop. This removes any dependency on UDP support of the selected proxy for DNS.
- Starts conservatively as IPv4-only to avoid IPv6 black-hole behavior while validating the Android data path.
- Marks VpnService + Xray as connected as soon as native startup succeeds. Internet verification runs asynchronously and reports either VERIFIED or WARNING instead of blocking the Connect button for 25+ seconds.
- Uses actual network DNS first, with public DNS only as a last-resort fallback.
