#!/usr/bin/env bash
# Requires the ASAN test probe from run-native-safety.sh.
set -euo pipefail
[[ $# == 1 ]] || { echo "usage: $0 <artifact-root>" >&2; exit 2; }
NATIVE_SOURCE="$(cd "$(dirname "$0")/.." && pwd)"
ARTIFACTS="$(cd "$1" && pwd)"
PROJECT="$NATIVE_SOURCE/tests/managed/NativeHandleSafety.csproj"
dotnet build "$PROJECT" --artifacts-path "$ARTIFACTS/managed" \
  --disable-build-servers -m:1 /p:UseSharedCompilation=false > "$ARTIFACTS/managed-build.log" 2>&1
export DYLD_INSERT_LIBRARIES="$(/usr/bin/xcrun clang -print-file-name=libclang_rt.asan_osx_dynamic.dylib)"
export ASAN_OPTIONS=detect_leaks=0:abort_on_error=1:use_sigaltstack=0
dotnet "$ARTIFACTS/managed/bin/NativeHandleSafety/debug/NativeHandleSafety.dll" \
  "$ARTIFACTS/asan/tests/libnative-safety.dylib" > "$ARTIFACTS/managed-tests.log" 2>&1
cat "$ARTIFACTS/managed-tests.log"
/usr/bin/shasum -a 256 "$ARTIFACTS/managed/bin/NativeHandleSafety/debug/NativeHandleSafety.dll" \
  "$NATIVE_SOURCE"/../AAXClean.Codecs/Interop/Native{Decode,AacDecode,Ec3Decode,Ac4Decode,AacEncode}.cs \
  "$NATIVE_SOURCE/tests/managed/Program.cs" > "$ARTIFACTS/managed-sha256.txt"
