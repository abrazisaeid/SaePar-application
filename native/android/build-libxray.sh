#!/usr/bin/env bash
set -euo pipefail
# Official libXray README recommends its build script.
# Requires git, Go, Python 3, gomobile toolchain/Android SDK.
git clone https://github.com/XTLS/libXray.git libXray-src
cd libXray-src
python3 build/main.py android
