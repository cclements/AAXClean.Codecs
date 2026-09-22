#!/usr/bin/env bash
# macOS ASan execution against the real native-safety probe and exact managed DLL.
set -euo pipefail
[[ $# == 4 ]] || { echo "usage: $0 <harness.dll> <probe.dylib> <synthetic.m4a> <log-directory>" >&2; exit 2; }
HARNESS="$1"
PROBE="$2"
INPUT="$3"
LOGS="$4"
mkdir -p "$LOGS"
export DYLD_INSERT_LIBRARIES="$(/usr/bin/xcrun clang -print-file-name=libclang_rt.asan_osx_dynamic.dylib)"
export ASAN_OPTIONS=detect_leaks=0:abort_on_error=1:use_sigaltstack=0
for case_name in single-complete multipart-complete single-construction-failure multipart-construction-failure single-flush-failure multipart-flush-failure single-write-failure multipart-write-failure chain-single-failure chain-multipart-failure; do
  dotnet "$HARNESS" "$PROBE" "$INPUT" "$case_name" > "$LOGS/$case_name.log" 2>&1
  cat "$LOGS/$case_name.log"
done
printf '%s\n' 'PASS 10/10 AAC pipeline native lifetime cases'
