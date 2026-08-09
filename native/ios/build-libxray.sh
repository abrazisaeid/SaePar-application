#!/usr/bin/env bash
set -euo pipefail
# Run on macOS with Xcode + iOS Simulator Runtime.
git clone https://github.com/XTLS/libXray.git libXray-src
cd libXray-src
python3 build/main.py apple gomobile
