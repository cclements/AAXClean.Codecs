#!/usr/bin/env bash
# Relink current wrapper against immutable, already-attested safety dependencies.
set -euo pipefail
[[ $# == 3 ]] || { echo "usage: $0 <safety-artifacts> <drain-artifacts> <asan|release>" >&2; exit 2; }
NATIVE_SOURCE="$(cd "$(dirname "$0")/.." && pwd)"
SAFETY="$(cd "$1" && pwd)"
mkdir -p "$2"
ARTIFACTS="$(cd "$2" && pwd)"
MODE="$3"
case "$MODE" in
  asan) FLAGS=(-O1 -g -fsanitize=address -fno-omit-frame-pointer) ;;
  release) FLAGS=(-O2 -g) ;;
  *) exit 2 ;;
esac
PREFIX="$SAFETY/$MODE/prefix"
BUILD="$ARTIFACTS/$MODE"
mkdir -p "$BUILD"
python3 - "$SAFETY" "$MODE" <<'PY'
from pathlib import Path
import hashlib,sys
root,mode=Path(sys.argv[1]),sys.argv[2]
rows=(root/mode/'sha256.txt').read_text().splitlines()
checked=[]
for row in rows:
    expected,name=row.split(maxsplit=1)
    path=Path(name)
    if path.suffix=='.a' or '/sources/' in name or '/librempeg/' in name:
        assert hashlib.sha256(path.read_bytes()).hexdigest()==expected, path
        checked.append(str(path))
assert len([p for p in checked if p.endswith('.a')]) >= 6, checked
print('Verified immutable linked dependencies and header/config identities:',len(checked))
PY
CC="$(/usr/bin/xcrun --find clang)"
export SDKROOT="$(/usr/bin/xcrun --show-sdk-path)"
export MACOSX_DEPLOYMENT_TARGET=14.0
"$CC" "${FLAGS[@]}" -fPIC -I"$PREFIX/include" -c "$NATIVE_SOURCE/AacDecoder.c" -o "$BUILD/AacDecoder.o"
"$CC" "${FLAGS[@]}" -fPIC -I"$PREFIX/include" -c "$NATIVE_SOURCE/AacEncoder.c" -o "$BUILD/AacEncoder.o"
"$CC" "${FLAGS[@]}" -dynamiclib -Wl,-install_name,@rpath/libaaxcleannative.dylib \
  "$BUILD/AacDecoder.o" "$BUILD/AacEncoder.o" \
  "$PREFIX/lib/libavcodec.a" "$PREFIX/lib/libavfilter.a" "$PREFIX/lib/libswresample.a" "$PREFIX/lib/libavutil.a" \
  "$PREFIX/lib/libfdk-aac.a" "$PREFIX/lib/libmp3lame.a" -lm -o "$BUILD/libaaxcleannative.dylib"
/usr/bin/otool -L "$BUILD/libaaxcleannative.dylib" > "$BUILD/linked-libraries.txt"
/usr/bin/nm -gU "$BUILD/libaaxcleannative.dylib" > "$BUILD/exports.txt"
/usr/bin/shasum -a 256 "$BUILD/libaaxcleannative.dylib" "$NATIVE_SOURCE/AacDecoder.c" \
  "$NATIVE_SOURCE/AAXCleanNative.h" "$NATIVE_SOURCE/AacEncoder.c" > "$BUILD/sha256.txt"
"$CC" --version > "$BUILD/compiler.txt"
