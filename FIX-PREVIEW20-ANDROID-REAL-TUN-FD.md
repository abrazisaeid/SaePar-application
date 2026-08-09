# Preview 20 - Android real TUN fd injection

Preview 19 could start VpnService and libXray but real traffic still timed out.
The important change in Preview 20 is how the Android TUN descriptor reaches Xray.

## Root cause
Previous previews initialized libXray during config tests, reserved a `/dev/null`
file descriptor, and later used `dup2()` to replace that descriptor with the real
VpnService TUN. On real devices this could leave Xray running while it did not
actually consume packets from the live Android TUN.

Xray-core v26.7.28 has a root JSON `env` object. The Android fd is now injected
immediately before `runXrayFromJson` as:

```json
"env": {
  "xray.tun.fd": "<actual ParcelFileDescriptor.fd>"
}
```

This is safe even when the Go runtime was initialized earlier by Full-Test.

The Java bridge now only registers both libXray DialerController and
ListenerController, protects Xray sockets with VpnService.protect(), and configures
libXray DNS. It no longer calls dup2 or publishes XRAY_TUN_FD itself.

The Home status also prints the actual fd handed to Xray so device testing can
confirm the runtime path.
