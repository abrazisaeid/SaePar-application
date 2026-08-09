# iOS Packet Tunnel bridge

The MAUI shell/shared core run on iOS, but a real device tunnel needs a Network Extension target.

Target architecture:
1. Build XTLS/libXray for Apple (`LibXray.xcframework`).
2. Add a Packet Tunnel Provider extension on a Mac/Xcode-capable build host.
3. Use `NEPacketTunnelProvider` to configure routes/DNS and obtain the utun fd.
4. Put `xray.tun.fd` in the Xray config root `env` object before invoking libXray.
5. Sign the app + extension with the required Network Extension entitlement.

Per-app VPN on unmanaged consumer iOS is not exposed like Android's allowed-app list; keep website/domain routing in the shared Xray config.
