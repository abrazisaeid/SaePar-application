# Preview 16 — Android connection feedback and VPN consent diagnostics

Changes:
- Shows live connection stages next to the Connect button.
- Explicitly explains when `VpnService.Prepare()` returns null because permission was already granted (no system consent page is expected in that case).
- Adds a 60-second bounded VPN permission wait instead of allowing an indefinite wait.
- Adds an `OnResume()` fallback for OEM Android builds that fail to deliver the classic activity result reliably.
- Shows a success dialog after the service has established TUN and libXray has started.
- Shows native service/TUN/Xray errors in the same connection status panel.
- Adds an **Android VPN settings** button.
- Adds `Builder.SetConfigureIntent()` so system VPN UI can return to SaePar Tunnel.
