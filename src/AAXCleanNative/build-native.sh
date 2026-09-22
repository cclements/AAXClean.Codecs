#!/usr/bin/env bash
# Matching-host, pinned development build. Never writes bins/ or uploads binaries.
set -euo pipefail
[[ $# == 2 || $# == 3 ]] || { echo "usage: $0 <artifact-root> <release|asan> [rid]" >&2; exit 2; }
NATIVE_SOURCE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$1"
ARTIFACTS="$(cd "$1" && pwd)"
MODE="$2"
case "$MODE" in
  release) FLAGS="-O2 -g" ;;
  asan) FLAGS="-O1 -g -fsanitize=address -fno-omit-frame-pointer" ;;
  *) echo "unsupported build mode: $MODE" >&2; exit 2 ;;
esac
MACHINE="$(python3 -c 'import platform; print(platform.machine().lower())')"
case "$MACHINE" in
  arm64|aarch64) ARCH=arm64; FFMPEG_ARCH=aarch64 ;;
  x86_64|amd64) ARCH=x64; FFMPEG_ARCH=x86_64 ;;
  *) echo "unsupported native architecture: $MACHINE" >&2; exit 2 ;;
esac
case "$(uname -s)" in
  Darwin) PLATFORM=osx ;;
  Linux) PLATFORM=linux ;;
  MINGW*|MSYS*) PLATFORM=win ;;
  *) echo 'unsupported build host' >&2; exit 2 ;;
esac
RID="$PLATFORM-$ARCH"
[[ ${3:-$RID} == "$RID" ]] || { echo "requested RID does not match host $RID" >&2; exit 2; }
[[ "$PLATFORM" != win || "$MODE" == release ]] || { echo 'Windows ASan is not admitted by this recipe' >&2; exit 2; }
BUILD="$ARTIFACTS/$MODE"
PREFIX="$BUILD/prefix"
mkdir -p "$BUILD" "$PREFIX"
# A failed rebuild cannot leave a successful receipt for previous bytes.
rm -f "$BUILD/build-receipt.json" "$BUILD/native-verification.json"
python3 "$NATIVE_SOURCE/prepare-native-sources.py" "$ARTIFACTS"
exec > "$BUILD/build.log" 2>&1
export ZERO_AR_DATE=1 SOURCE_DATE_EPOCH=1788739200
CMAKE="${CMAKE:-cmake}"
JOBS="${JOBS:-8}"
CMAKE_OPTIONS=()
HOST_OPTIONS=()
THREAD_OPTIONS=(--enable-pthreads)
LINK_OPTIONS=()
TEST_LINK_OPTIONS=()
GENERATOR='Unix Makefiles'
case "$PLATFORM" in
  osx)
    CC="$(xcrun --find clang)"; CXX="$(xcrun --find clang++)"
    SDK="$(xcrun --show-sdk-path)"
    export SDKROOT="$SDK" MACOSX_DEPLOYMENT_TARGET=14.0
    export CPPFLAGS="-isysroot $SDK"
    FLAGS="$FLAGS -isysroot $SDK"
    CMAKE_OPTIONS=(-DCMAKE_OSX_ARCHITECTURES="$MACHINE" -DCMAKE_OSX_SYSROOT="$SDK" -DCMAKE_OSX_DEPLOYMENT_TARGET=14.0)
    HOST_OPTIONS=(--host-cflags="-isysroot $SDK" --host-ldflags="-isysroot $SDK")
    TARGET_OS=darwin
    TRIPLE="$FFMPEG_ARCH-apple-darwin"
    NATIVE_FILE=libaaxcleannative.dylib
    LINK_OPTIONS=(-dynamiclib "-Wl,-install_name,@rpath/libaaxcleannative.dylib")
    ;;
  linux)
    CC="${CC:-gcc}"; CXX="${CXX:-g++}"
    TARGET_OS=linux
    TRIPLE="$($CC -dumpmachine)"
    NATIVE_FILE=libaaxcleannative.so
    LINK_OPTIONS=(-shared "-Wl,--no-undefined" "-Wl,-Bsymbolic" "-Wl,-soname,libaaxcleannative.so")
    TEST_LINK_OPTIONS=(-ldl)
    ;;
  win)
    CC="${CC:-cc}"; CXX="${CXX:-c++}"
    TARGET_OS=mingw32
    TRIPLE="$($CC -dumpmachine)"
    NATIVE_FILE=aaxcleannative.dll
    GENERATOR='MSYS Makefiles'
    THREAD_OPTIONS=(--disable-pthreads --enable-w32threads)
    LINK_OPTIONS=(-shared -static "-Wl,--export-all-symbols" "-Wl,--no-undefined")
    ;;
esac
export CC CXX
export CFLAGS="$FLAGS -fPIC" CXXFLAGS="$FLAGS -fPIC" LDFLAGS="$FLAGS"
{
  printf 'rid=%s\nmode=%s\nflags=%s\n' "$RID" "$MODE" "$FLAGS"
  "$CC" --version
  "$CMAKE" --version
  make --version
  uname -a
  if [[ "$PLATFORM" == osx ]]; then xcrun --show-sdk-version; sw_vers; fi
  if [[ -n ${ImageVersion:-} ]]; then printf 'runner_image=%s\n' "$ImageVersion"; fi
  if [[ "$PLATFORM" == win ]]; then pacman -Q; fi
} > "$BUILD/toolchain.txt"
# Read names from the hashed source manifest instead of duplicating revision pins.
source_dir() { python3 -c 'import json,sys; print(next(s["directory"] for s in json.load(open(sys.argv[1]))["sources"] if s["name"]==sys.argv[2]))' "$NATIVE_SOURCE/native-sources.json" "$1"; }
FDK="$ARTIFACTS/sources/$(source_dir fdk-aac)"
LAME="$ARTIFACTS/sources/$(source_dir lame)"
LIBREMPEG="$ARTIFACTS/sources/$(source_dir librempeg)"
set -x
"$CMAKE" -S "$FDK" -B "$BUILD/fdk" -G "$GENERATOR" -DCMAKE_BUILD_TYPE=RelWithDebInfo \
  -DBUILD_SHARED_LIBS=OFF -DBUILD_PROGRAMS=OFF -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
  -DCMAKE_INSTALL_PREFIX="$PREFIX" -DCMAKE_C_COMPILER="$CC" -DCMAKE_CXX_COMPILER="$CXX" \
  -DCMAKE_C_FLAGS_RELWITHDEBINFO="$FLAGS" -DCMAKE_CXX_FLAGS_RELWITHDEBINFO="$FLAGS" ${CMAKE_OPTIONS[@]+"${CMAKE_OPTIONS[@]}"}
"$CMAKE" --build "$BUILD/fdk" --parallel "$JOBS"
"$CMAKE" --install "$BUILD/fdk"
mkdir -p "$BUILD/lame"
(
  cd "$BUILD/lame"
  "$LAME/configure" --prefix="$PREFIX" --host="$TRIPLE" --build="$TRIPLE" \
    --disable-shared --enable-static --disable-frontend --disable-decoder --disable-dependency-tracking
  make -j"$JOBS"
  make install
)
mkdir -p "$BUILD/librempeg"
(
  cd "$BUILD/librempeg"
  "$LIBREMPEG/configure" --prefix="$PREFIX" --cc="$CC" --cxx="$CXX" --arch="$FFMPEG_ARCH" --target-os="$TARGET_OS" \
    ${HOST_OPTIONS[@]+"${HOST_OPTIONS[@]}"} --pkg-config=false --extra-cflags="$CFLAGS -I$PREFIX/include" \
    --extra-ldflags="$LDFLAGS -L$PREFIX/lib" \
    --disable-autodetect --disable-everything --disable-network --disable-programs --disable-doc \
    --disable-shared --enable-static --enable-pic --disable-asm "${THREAD_OPTIONS[@]}" \
    --disable-avdevice --disable-swscale --disable-avformat \
    --enable-avcodec --enable-avfilter --enable-swresample \
    --enable-libfdk_aac --enable-nonfree --enable-decoder=libfdk_aac \
    --enable-encoder=libfdk_aac --enable-decoder=eac3 --enable-decoder=ac4 \
    --enable-libmp3lame --enable-encoder=libmp3lame --disable-symver
  make -j"$JOBS"
  make install
)
LIBS=("$PREFIX/lib/libavcodec.a" "$PREFIX/lib/libavfilter.a" "$PREFIX/lib/libswresample.a" "$PREFIX/lib/libavutil.a" "$PREFIX/lib/libfdk-aac.a" "$PREFIX/lib/libmp3lame.a")
if [[ "$PLATFORM" != osx ]]; then LIBS=("-Wl,--start-group" "${LIBS[@]}" "-Wl,--end-group"); fi
if [[ "$PLATFORM" == linux ]]; then LIBS+=(-pthread); fi
if [[ "$PLATFORM" == win ]]; then LIBS+=(-lbcrypt); fi
LIBS+=(-lm)
# FLAGS intentionally contains the individual compiler options selected above.
# shellcheck disable=SC2086
"$CC" $CFLAGS -I"$PREFIX/include" -c "$NATIVE_SOURCE/AacDecoder.c" -o "$BUILD/AacDecoder.o"
# shellcheck disable=SC2086
"$CC" $CFLAGS -I"$PREFIX/include" -c "$NATIVE_SOURCE/AacEncoder.c" -o "$BUILD/AacEncoder.o"
# shellcheck disable=SC2086
"$CC" $FLAGS "${LINK_OPTIONS[@]}" -o "$BUILD/$NATIVE_FILE" "$BUILD/AacDecoder.o" "$BUILD/AacEncoder.o" "${LIBS[@]}"
# The named-library test resolves exports from the actual generated payload.
# shellcheck disable=SC2086
"$CC" $CFLAGS -I"$PREFIX/include" "$NATIVE_SOURCE/tests/drain-tests.c" ${TEST_LINK_OPTIONS[@]+"${TEST_LINK_OPTIONS[@]}"} -o "$BUILD/drain-tests"
# shellcheck disable=SC2086
"$CC" $CFLAGS -I"$PREFIX/include" "$NATIVE_SOURCE/tests/drain-format-tests.c" "${LIBS[@]}" -o "$BUILD/drain-format-tests"
set +x
case "$PLATFORM" in
  osx) nm -gU "$BUILD/$NATIVE_FILE" > "$BUILD/exports.txt"; otool -L "$BUILD/$NATIVE_FILE" > "$BUILD/linked-libraries.txt" ;;
  linux) nm -D --defined-only "$BUILD/$NATIVE_FILE" > "$BUILD/exports.txt"; ldd "$BUILD/$NATIVE_FILE" > "$BUILD/linked-libraries.txt" ;;
  win) objdump -p "$BUILD/$NATIVE_FILE" > "$BUILD/exports.txt"; cp "$BUILD/exports.txt" "$BUILD/linked-libraries.txt" ;;
esac
python3 "$NATIVE_SOURCE/native-receipt.py" build --source "$NATIVE_SOURCE" --build "$BUILD" --rid "$RID" --mode "$MODE"
printf 'Built pinned development payload: %s\n' "$BUILD/$NATIVE_FILE"
