#!/usr/bin/env bash
# Compatibility entry point for the original local safety lane.
set -euo pipefail
[[ $# == 2 && $(uname -s) == Darwin && $(uname -m) == arm64 ]] || { echo "usage: $0 <artifact-root> <asan|release> (macOS arm64)" >&2; exit 2; }
exec bash "$(dirname "$0")/build-native.sh" "$1" "$2" osx-arm64
