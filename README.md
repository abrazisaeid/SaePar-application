# SaePar Tunnel — v2.0 preview21 source snapshot

This repository currently stores the latest generated source snapshot as:

`SaeParTunnel-CrossPlatform-v2.0-preview21-source.zip`

## Open locally

1. Clone this repository.
2. Extract `SaeParTunnel-CrossPlatform-v2.0-preview21-source.zip` into a working folder.
3. Open `SaeParTunnel.CrossPlatform.sln` in Visual Studio 2022, or open the extracted folder in VS Code + Codex.

## Android libXray dependency

The large `libXray.aar` runtime is intentionally not committed because it is about 96 MB. Use the official XTLS/libXray v26.7.28 Android AAR and place it at:

`src/SaeParTunnel.AndroidBinding/Libraries/libXray.aar`

Expected SHA-256:

`4708a361a74f7e955635dbe3661cefb459bdc867423c3b1826a2c5a6ea4ac77d`

The smaller `SaeParXrayBridge.aar` is already included inside the source ZIP.
