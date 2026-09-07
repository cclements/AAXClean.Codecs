#!/usr/bin/env bash
# Requires build-osx-arm64.sh <artifact-root> asan to have completed.
set -euo pipefail
[[ $# == 1 ]] || { echo "usage: $0 <artifact-root>" >&2; exit 2; }
NATIVE_SOURCE="$(cd "$(dirname "$0")/.." && pwd)"
ARTIFACTS="$(cd "$1" && pwd)"
BUILD="$ARTIFACTS/asan"
PREFIX="$BUILD/prefix"
CC="$(/usr/bin/xcrun --find clang)"
export SDKROOT="$(/usr/bin/xcrun --show-sdk-path)"
export MACOSX_DEPLOYMENT_TARGET=14.0
export ASAN_OPTIONS=detect_leaks=0:abort_on_error=1
FLAGS=(-O1 -g -fsanitize=address -fno-omit-frame-pointer -I"$PREFIX/include")
LIBS=("$PREFIX/lib/libavcodec.a" "$PREFIX/lib/libavfilter.a" "$PREFIX/lib/libswresample.a" "$PREFIX/lib/libavutil.a" "$PREFIX/lib/libfdk-aac.a" "$PREFIX/lib/libmp3lame.a" -lm)
mkdir -p "$BUILD/tests"
"$CC" "${FLAGS[@]}" "$NATIVE_SOURCE/tests/native-safety.c" "${LIBS[@]}" -o "$BUILD/tests/native-safety"
"$CC" "${FLAGS[@]}" -DBUILD_MANAGED_PROBE -dynamiclib \
  "$NATIVE_SOURCE/tests/native-safety.c" "${LIBS[@]}" -o "$BUILD/tests/libnative-safety.dylib"
for case_name in abi extradata-boundary packet-boundary malformed-packets packet-errors failed-opens; do
  "$BUILD/tests/native-safety" "$case_name" > "$BUILD/tests/$case_name.log" 2>&1
  cat "$BUILD/tests/$case_name.log"
done
/usr/bin/shasum -a 256 "$BUILD/tests/native-safety" "$BUILD/tests/libnative-safety.dylib" \
  "$NATIVE_SOURCE/tests/native-safety.c" > "$BUILD/tests/sha256.txt"
