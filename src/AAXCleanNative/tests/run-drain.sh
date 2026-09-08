#!/usr/bin/env bash
# Local pinned osx-arm64 lane. Does not replace any shipping runtime payload.
set -euo pipefail
[[ $# == 2 ]] || { echo "usage: $0 <safety-artifacts> <new-drain-artifacts>" >&2; exit 2; }
NATIVE_SOURCE="$(cd "$(dirname "$0")/.." && pwd)"
SAFETY="$(cd "$1" && pwd)"
mkdir -p "$2"
ARTIFACTS="$(cd "$2" && pwd)"
python3 "$NATIVE_SOURCE/tests/generate-drain-fixtures.py" "$ARTIFACTS/fixtures" > "$ARTIFACTS/fixtures.log"
CC="$(/usr/bin/xcrun --find clang)"
export SDKROOT="$(/usr/bin/xcrun --show-sdk-path)"
export MACOSX_DEPLOYMENT_TARGET=14.0
export ASAN_OPTIONS=detect_leaks=0:abort_on_error=1
for MODE in release asan; do
    bash "$NATIVE_SOURCE/tests/build-drain.sh" "$SAFETY" "$ARTIFACTS" "$MODE" > "$ARTIFACTS/build-$MODE.log" 2>&1
    BUILD="$ARTIFACTS/$MODE"
    PREFIX="$SAFETY/$MODE/prefix"
    FLAGS=(-O2)
    if [[ "$MODE" == asan ]]; then FLAGS=(-O1 -g -fsanitize=address -fno-omit-frame-pointer); fi
    LIBS=("$PREFIX/lib/libavcodec.a" "$PREFIX/lib/libavfilter.a" "$PREFIX/lib/libswresample.a" "$PREFIX/lib/libavutil.a" "$PREFIX/lib/libfdk-aac.a" "$PREFIX/lib/libmp3lame.a" -lm)
    for TEST in drain-tests drain-format-tests native-safety; do
        "$CC" "${FLAGS[@]}" -I"$PREFIX/include" "$NATIVE_SOURCE/tests/$TEST.c" "${LIBS[@]}" -o "$BUILD/$TEST"
    done
    "$BUILD/drain-format-tests" > "$BUILD/format-tests.log" 2>&1
    for CODEC in aac eac3 eac3-paired; do
        "$BUILD/drain-tests" "$BUILD/libaaxcleannative.dylib" "$ARTIFACTS/fixtures" "$CODEC" "$BUILD/$CODEC.s16" v2 > "$BUILD/$CODEC.log" 2>&1
    done
    "$BUILD/drain-tests" "$BUILD/libaaxcleannative.dylib" "$ARTIFACTS/fixtures" eac3-rate-change "$BUILD/rejected.s16" reject-change > "$BUILD/rate-change.log" 2>&1
    for CASE in abi extradata-boundary packet-boundary malformed-packets packet-errors failed-opens; do
        "$BUILD/native-safety" "$CASE" > "$BUILD/safety-$CASE.log" 2>&1
    done
    echo "$MODE: fixed-format drain, format-change rejection and six safety suites passed"
done
python3 - "$ARTIFACTS" "$NATIVE_SOURCE" <<'PY'
from pathlib import Path
import hashlib,json,sys
root,source=map(Path,sys.argv[1:])
counts={}
for mode in ['release','asan']:
    for codec,expected in [('aac',3344),('eac3',7168),('eac3-paired',7168)]:
        result=json.loads((root/mode/f'{codec}.log').read_text())
        assert result['samplesPerChannel']==expected,result
        reference=root/'fixtures'/('aac-reference.s16' if codec=='aac' else 'eac3-reference.s16')
        assert (root/mode/f'{codec}.s16').stat().st_size==reference.stat().st_size
        counts[f'{mode}-{codec}']=result
for codec in ['aac','eac3','eac3-paired']:
    assert (root/'release'/f'{codec}.s16').read_bytes()==(root/'asan'/f'{codec}.s16').read_bytes()
hashes={str(p.relative_to(root)):hashlib.sha256(p.read_bytes()).hexdigest()
        for p in root.rglob('*') if p.is_file() and p.name!='verification.json'}
sources={str(p.relative_to(source)):hashlib.sha256(p.read_bytes()).hexdigest()
         for p in [source/'AacDecoder.c',source/'AAXCleanNative.h',source/'AacEncoder.c',*sorted((source/'tests').glob('*.*'))] if p.is_file()}
(root/'verification.json').write_text(json.dumps(dict(counts=counts,hashes=hashes,sources=sources,
    disposition='NONFREE/UNREDISTRIBUTABLE local osx-arm64 development; no shipping payload replacement'),indent=2)+'\n')
PY
