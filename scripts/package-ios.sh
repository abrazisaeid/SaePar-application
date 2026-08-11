#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
APP_PROJECT="$REPO_ROOT/src/SaeParTunnel.App/SaeParTunnel.App.csproj"
CONFIGURATION="${CONFIGURATION:-Release}"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Signed iOS packaging must run on macOS with Xcode installed." >&2
  exit 1
fi

required_variables=(
  IOS_SIGNING_KEY
  IOS_APP_PROVISIONING_PROFILE
  IOS_EXTENSION_PROVISIONING_PROFILE
)

for variable_name in "${required_variables[@]}"; do
  if [[ -z "${!variable_name:-}" ]]; then
    echo "Missing required environment variable: $variable_name" >&2
    exit 1
  fi
done

project_version="$({
  sed -n 's|.*<ApplicationDisplayVersion>\([^<]*\)</ApplicationDisplayVersion>.*|\1|p' "$APP_PROJECT"
} | head -n 1)"
project_build_version="$({
  sed -n 's|.*<ApplicationVersion>\([^<]*\)</ApplicationVersion>.*|\1|p' "$APP_PROJECT"
} | head -n 1)"

release_tag="${1:-v$project_version}"
release_tag="v${release_tag#v}"
asset_version="${release_tag#v}"

if [[ "$asset_version" != "$project_version" ]]; then
  echo "Release tag $release_tag does not match project version $project_version." >&2
  exit 1
fi

release_root="$REPO_ROOT/artifacts/release/$release_tag"
ipa_path="$release_root/SaeParTunnel-$asset_version-ios.ipa"
mkdir -p "$release_root"
rm -f "$ipa_path"

"$REPO_ROOT/native/ios/fetch-libxray.sh"

publish_arguments=(
  publish "$APP_PROJECT"
  -f net9.0-ios
  -c "$CONFIGURATION"
  -r ios-arm64
  -p:ArchiveOnBuild=true
  -p:BuildIpa=true
  "-p:IpaPackagePath=$ipa_path"
  "-p:IosSigningKey=$IOS_SIGNING_KEY"
  "-p:IosAppProvisioningProfile=$IOS_APP_PROVISIONING_PROFILE"
  "-p:IosExtensionProvisioningProfile=$IOS_EXTENSION_PROVISIONING_PROFILE"
)

if [[ -n "${IOS_SIGNING_KEYCHAIN:-}" ]]; then
  publish_arguments+=("-p:IosSigningKeychain=$IOS_SIGNING_KEYCHAIN")
fi

echo "Publishing signed iOS package for $release_tag..."
publish_marker="$(mktemp)"
trap 'rm -f "$publish_marker"' EXIT
dotnet "${publish_arguments[@]}"

if [[ ! -f "$ipa_path" ]]; then
  generated_ipa="$(find "$REPO_ROOT/src/SaeParTunnel.App/bin/$CONFIGURATION/net9.0-ios/ios-arm64" \
    -type f -name '*.ipa' -newer "$publish_marker" -print 2>/dev/null | head -n 1 || true)"
  if [[ -z "$generated_ipa" ]]; then
    echo "The iOS publish completed without producing an IPA." >&2
    exit 1
  fi

  cp "$generated_ipa" "$ipa_path"
fi

unzip -tq "$ipa_path" >/dev/null
validation_root="$(mktemp -d)"
trap 'rm -f "$publish_marker"; rm -rf "$validation_root"' EXIT
unzip -q "$ipa_path" -d "$validation_root"

if [[ ! -d "$validation_root/Payload" ]]; then
  echo "The IPA does not contain a Payload directory." >&2
  exit 1
fi

app_count="$(find "$validation_root/Payload" -maxdepth 1 -type d -name '*.app' | wc -l | tr -d ' ')"
app_bundle="$(find "$validation_root/Payload" -maxdepth 1 -type d -name '*.app' | head -n 1)"
if [[ "$app_count" != "1" || -z "$app_bundle" ]]; then
  echo "Expected exactly one app bundle in the IPA, found $app_count." >&2
  exit 1
fi

if [[ ! -d "$app_bundle/PlugIns" ]]; then
  echo "The IPA does not contain an app-extension directory." >&2
  exit 1
fi

extension_count="$(find "$app_bundle/PlugIns" -maxdepth 1 -type d -name '*.appex' | wc -l | tr -d ' ')"
extension_bundle="$(find "$app_bundle/PlugIns" -maxdepth 1 -type d -name '*.appex' | head -n 1)"
if [[ "$extension_count" != "1" || -z "$extension_bundle" ]]; then
  echo "Expected exactly one Packet Tunnel extension in the IPA, found $extension_count." >&2
  exit 1
fi

app_identifier="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$app_bundle/Info.plist")"
extension_identifier="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$extension_bundle/Info.plist")"
app_version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$app_bundle/Info.plist")"
extension_version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$extension_bundle/Info.plist")"
app_build_version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$app_bundle/Info.plist")"
extension_build_version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$extension_bundle/Info.plist")"
if [[ "$app_identifier" != "com.saepar.tunnel" ]]; then
  echo "Unexpected app bundle identifier: $app_identifier" >&2
  exit 1
fi
if [[ "$extension_identifier" != "com.saepar.tunnel.packet-tunnel" ]]; then
  echo "Unexpected extension bundle identifier: $extension_identifier" >&2
  exit 1
fi
if [[ "$app_version" != "$project_version" || "$extension_version" != "$project_version" ]]; then
  echo "App and extension release versions must both be $project_version." >&2
  exit 1
fi
if [[ "$app_build_version" != "$project_build_version" || "$extension_build_version" != "$project_build_version" ]]; then
  echo "App and extension build versions must both be $project_build_version." >&2
  exit 1
fi

for signed_bundle in "$app_bundle" "$extension_bundle"; do
  if [[ ! -f "$signed_bundle/embedded.mobileprovision" ]]; then
    echo "Missing embedded provisioning profile in $signed_bundle." >&2
    exit 1
  fi

  entitlements_path="$validation_root/$(basename "$signed_bundle").entitlements.plist"
  codesign -d --entitlements :- "$signed_bundle" > "$entitlements_path" 2>/dev/null
  packet_tunnel_entitlement="$(/usr/libexec/PlistBuddy \
    -c 'Print :com.apple.developer.networking.networkextension:0' \
    "$entitlements_path")"
  if [[ "$packet_tunnel_entitlement" != "packet-tunnel-provider" ]]; then
    echo "Missing packet-tunnel-provider entitlement in $signed_bundle." >&2
    exit 1
  fi
done

codesign --verify --deep --strict --verbose=2 "$app_bundle"
echo "Validated iOS IPA: $ipa_path"
