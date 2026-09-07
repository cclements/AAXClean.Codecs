#!/usr/bin/env bash
# Compile a historical wrapper against the SAME pinned/instrumented libraries.
set -euo pipefail
[[ $# == 2 ]] || { echo "usage: $0 <artifact-root> <baseline-git-revision>" >&2; exit 2; }
NATIVE_SOURCE="$(cd "$(dirname "$0")/.." && pwd)"
ARTIFACTS="$(cd "$1" && pwd)"
BUILD="$ARTIFACTS/asan"
PREFIX="$BUILD/prefix"
BASELINE="$ARTIFACTS/baseline"
mkdir -p "$BASELINE"
git -C "$NATIVE_SOURCE" rev-parse "$2^{commit}" > "$BASELINE/revision.txt"
git -C "$NATIVE_SOURCE" show "$2:src/AAXCleanNative/AacDecoder.c" > "$BASELINE/AacDecoder.c"
CC="$(/usr/bin/xcrun --find clang)"
export SDKROOT="$(/usr/bin/xcrun --show-sdk-path)"
export MACOSX_DEPLOYMENT_TARGET=14.0
export ASAN_OPTIONS=detect_leaks=0:abort_on_error=1
"$CC" -O1 -g -fsanitize=address -fno-omit-frame-pointer \
  -I"$PREFIX/include" -I"$NATIVE_SOURCE" "-DDECODER_SOURCE=\"$BASELINE/AacDecoder.c\"" \
  "$NATIVE_SOURCE/tests/native-safety.c" \
  "$PREFIX/lib/libavcodec.a" "$PREFIX/lib/libavfilter.a" "$PREFIX/lib/libswresample.a" "$PREFIX/lib/libavutil.a" \
  "$PREFIX/lib/libfdk-aac.a" "$PREFIX/lib/libmp3lame.a" -lm -o "$BASELINE/native-safety"
for case_name in extradata-boundary packet-boundary failed-opens; do
  set +e
  "$BASELINE/native-safety" "$case_name" > "$BASELINE/$case_name.log" 2>&1
  result=$?
  set -e
  [[ $result != 0 ]] || { echo "ERROR: baseline unexpectedly passed $case_name" >&2; exit 1; }
  case "$case_name" in
    extradata-boundary) pattern='AddressSanitizer: heap-buffer-overflow' ;;
    packet-boundary) pattern='Assertion failed:.*packet->buf && packet->data != borrowed' ;;
    failed-opens) pattern='AddressSanitizer: heap-use-after-free' ;;
  esac
  rg -q "$pattern" "$BASELINE/$case_name.log" || { echo "ERROR: unexpected baseline failure: $case_name" >&2; exit 1; }
  printf 'CONFIRMED regression witness %s: exit=%s; %s\n' "$case_name" "$result" "$pattern"
done
/usr/bin/shasum -a 256 "$BASELINE/AacDecoder.c" "$BASELINE/native-safety" > "$BASELINE/sha256.txt"
