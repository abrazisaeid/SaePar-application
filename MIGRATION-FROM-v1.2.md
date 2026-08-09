# Migration from SaePar Tunnel v1.2

The v1.2 WPF code was used as the source for the portable config model, parsers, GitHub source, Xray JSON builder, test behavior, filters, whitelist concepts, and Windows Xray runtime.

On Windows the new app intentionally uses `%LOCALAPPDATA%\SaeParTunnel`, so existing `settings.json`, `profiles.json`, and the downloaded Xray runtime can be reused.

The WPF UI itself was not copied. It was replaced with MAUI pages so the same XAML/C# app can render on desktop and mobile.
