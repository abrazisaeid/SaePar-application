# Preview 21 - Android data-plane alignment

- Align Android VpnService TUN layout with current OneXray: 198.18.0.1/32, MTU 1500, DNS 8.8.8.8.
- Keep the real ParcelFileDescriptor fd in root env[xray.tun.fd].
- Remove SaePar's custom direct-port-53 routing rule; Android DNS follows the proxy path.
- libXray internal DNS remains protected with VpnService.protect().
- Add two-stage diagnostics: raw TCP 1.1.1.1:443, then HTTPS probes.
- Status now reports fd/TUN/DNS so failures distinguish TUN transport from DNS/TLS.
