#!/usr/bin/env bash
# Isolated development build. Does not modify bins/ or install into the user's system.
# Usage: build-osx-arm64.sh <artifact-root> <asan|release>
set -euo pipefail
[[ $# == 2 && $(uname -s) == Darwin && $(uname -m) == arm64 ]] || { echo "usage: $0 <artifact-root> <asan|release> (macOS arm64)" >&2; exit 2; }
NATIVE_SOURCE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$1"
ARTIFACTS="$(cd "$1" && pwd)"
MODE="$2"
case "$MODE" in
  asan) FLAGS="-O1 -g -fsanitize=address -fno-omit-frame-pointer" ;;
  release) FLAGS="-O2 -g" ;;
  *) echo "unsupported build mode: $MODE" >&2; exit 2 ;;
esac
python3 "$NATIVE_SOURCE/prepare-native-sources.py" "$ARTIFACTS"
BUILD="$ARTIFACTS/$MODE"
PREFIX="$BUILD/prefix"
mkdir -p "$BUILD" "$PREFIX"
CC="$(/usr/bin/xcrun --find clang)"
CXX="$(/usr/bin/xcrun --find clang++)"
SDK="$(/usr/bin/xcrun --show-sdk-path)"
export SDKROOT="$SDK"
CMAKE="${CMAKE:-/opt/homebrew/bin/cmake}"
JOBS="${JOBS:-8}"
export ZERO_AR_DATE=1
export SOURCE_DATE_EPOCH=1788739200
export MACOSX_DEPLOYMENT_TARGET=14.0
export CPPFLAGS="-isysroot $SDK"
export CFLAGS="$FLAGS -fPIC -isysroot $SDK"
export CXXFLAGS="$CFLAGS"
export LDFLAGS="$FLAGS -isysroot $SDK"
export CC CXX
{
  "$CC" --version
  "$CMAKE" --version
  /usr/bin/xcrun --show-sdk-version
  /usr/bin/sw_vers
  printf 'mode=%s\nflags=%s\nsdk=%s\n' "$MODE" "$FLAGS" "$SDK"
} > "$BUILD/toolchain.txt"
FDK="$ARTIFACTS/sources/fdk-aac-7c83d08002332b2730c845eec3497e6bf585dd28"
LAME="$ARTIFACTS/sources/lame-3.100"
LIBREMPEG="$ARTIFACTS/sources/librempeg-33d3035699899c74708bd6cd387dfb4cc5aed26a"
"$CMAKE" -S "$FDK" -B "$BUILD/fdk" -DCMAKE_BUILD_TYPE=RelWithDebInfo -DBUILD_SHARED_LIBS=OFF \
  -DBUILD_PROGRAMS=OFF -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_OSX_SYSROOT="$SDK" -DCMAKE_OSX_DEPLOYMENT_TARGET=14.0 -DCMAKE_INSTALL_PREFIX="$PREFIX" \
  -DCMAKE_C_FLAGS_RELWITHDEBINFO="$FLAGS" -DCMAKE_CXX_FLAGS_RELWITHDEBINFO="$FLAGS"
"$CMAKE" --build "$BUILD/fdk" --parallel "$JOBS"
"$CMAKE" --install "$BUILD/fdk"
mkdir -p "$BUILD/lame"
(
  cd "$BUILD/lame"
  "$LAME/configure" --prefix="$PREFIX" --host=aarch64-apple-darwin --build=aarch64-apple-darwin \
    --disable-shared --enable-static --disable-frontend --disable-decoder --disable-dependency-tracking
  make -j"$JOBS"
  make install
)
mkdir -p "$BUILD/librempeg"
(
  cd "$BUILD/librempeg"
  # The pinned configure script supports direct header/symbol checks when pkg-config
  # is absent. Only the explicitly built prefix is supplied for external libraries.
  "$LIBREMPEG/configure" --prefix="$PREFIX" --cc="$CC" --cxx="$CXX" --arch=aarch64 --target-os=darwin \
    --host-cflags="-isysroot $SDK" --host-ldflags="-isysroot $SDK" \
    --pkg-config=/usr/bin/false --extra-cflags="$CFLAGS -I$PREFIX/include" \
    --extra-ldflags="$LDFLAGS -L$PREFIX/lib" \
    --disable-autodetect --disable-everything --disable-network --disable-programs --disable-doc \
    --disable-shared --enable-static --enable-pic --disable-asm --enable-pthreads \
    --disable-avdevice --disable-swscale --disable-avformat \
    --enable-avcodec --enable-avfilter --enable-swresample \
    --enable-libfdk_aac --enable-nonfree --enable-decoder=libfdk_aac \
    --enable-encoder=libfdk_aac --enable-decoder=eac3 --enable-decoder=ac4 \
    --enable-libmp3lame --enable-encoder=libmp3lame --disable-symver
  make -j"$JOBS"
  make install
)
"$CC" $FLAGS -fPIC -I"$PREFIX/include" -c "$NATIVE_SOURCE/AacDecoder.c" -o "$BUILD/AacDecoder.o"
"$CC" $FLAGS -fPIC -I"$PREFIX/include" -c "$NATIVE_SOURCE/AacEncoder.c" -o "$BUILD/AacEncoder.o"
"$CC" $FLAGS -dynamiclib -Wl,-install_name,@rpath/libaaxcleannative.dylib \
  -o "$BUILD/libaaxcleannative.dylib" "$BUILD/AacDecoder.o" "$BUILD/AacEncoder.o" \
  "$PREFIX/lib/libavcodec.a" "$PREFIX/lib/libavfilter.a" "$PREFIX/lib/libswresample.a" "$PREFIX/lib/libavutil.a" \
  "$PREFIX/lib/libfdk-aac.a" "$PREFIX/lib/libmp3lame.a" -lm
/usr/bin/otool -L "$BUILD/libaaxcleannative.dylib" > "$BUILD/linked-libraries.txt"
/usr/bin/nm -gU "$BUILD/libaaxcleannative.dylib" > "$BUILD/exports.txt"
/usr/bin/shasum -a 256 "$BUILD/libaaxcleannative.dylib" "$PREFIX"/lib/*.a \
  "$NATIVE_SOURCE/AAXCleanNative.h" "$NATIVE_SOURCE/AacDecoder.c" "$NATIVE_SOURCE/AacEncoder.c" \
  "$LIBREMPEG/libavcodec/avcodec.h" "$LIBREMPEG/libavcodec/packet.h" "$LIBREMPEG/libavcodec/packet.c" \
  "$BUILD/librempeg/config.h" "$BUILD/librempeg/ffbuild/config.mak" > "$BUILD/sha256.txt"
printf 'Built isolated %s payload: %s\n' "$MODE" "$BUILD/libaaxcleannative.dylib"
