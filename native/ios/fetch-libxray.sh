#!/usr/bin/env bash
set -euo pipefail

LIBXRAY_VERSION="v26.7.28"
LIBXRAY_SHA256="07f7ed7697277930e1c517755855950f594f41435b0dfc5917a66eea6278aeb9"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUTPUT_DIR="$SCRIPT_DIR/LibXray.xcframework"
CACHE_ROOT="${SAEPAR_CACHE_DIR:-${HOME}/.cache/saepar}"
CACHE_DIR="$CACHE_ROOT/libxray/$LIBXRAY_VERSION"
ARCHIVE="$CACHE_DIR/libxray-apple-cgo.zip"
DOWNLOAD_URL="https://github.com/XTLS/libXray/releases/download/$LIBXRAY_VERSION/libxray-apple-cgo.zip"

mkdir -p "$CACHE_DIR"

if [[ ! -f "$ARCHIVE" ]]; then
  echo "Downloading XTLS/libXray $LIBXRAY_VERSION for Apple platforms..."
  curl --fail --location --retry 3 --output "$ARCHIVE" "$DOWNLOAD_URL"
fi

echo "$LIBXRAY_SHA256  $ARCHIVE" | shasum --algorithm 256 --check

TEMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TEMP_DIR"' EXIT
unzip -q "$ARCHIVE" -d "$TEMP_DIR"

SOURCE_DIR="$TEMP_DIR/libxray-apple-cgo/LibXray.xcframework"
if [[ ! -f "$SOURCE_DIR/Info.plist" ]]; then
  echo "LibXray archive has an unexpected layout." >&2
  exit 1
fi

rm -rf "$OUTPUT_DIR"
mv "$SOURCE_DIR" "$OUTPUT_DIR"
echo "LibXray is ready at $OUTPUT_DIR"
