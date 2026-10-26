#!/usr/bin/env bash
# Builds libheif (HEIC/HEIF + AVIF) as one shared library with libde265 (HEVC) and dav1d (AV1) linked in
# statically, plus the license texts that ship with it.
# usage: build-codecs.sh <out-dir>
#   Linux: run in the ubuntu:20.04 build container so the glibc 2.31 baseline holds (needs g++-10, cmake, meson,
#   ninja, nasm on x64). macOS: local testing only; packages rely on ImageIO.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
source "$here/versions.env"
mkdir -p "$1/licenses"
OUT=$(cd "$1" && pwd)
WORK=${WORK:-$here/../../.cache/codecs-build}
mkdir -p "$WORK" && WORK=$(cd "$WORK" && pwd)
PREFIX=$WORK/prefix
jobs=$(getconf _NPROCESSORS_ONLN)

fetch() { # url dir
    [[ -d "$WORK/$2" ]] && return
    curl -fsSL "$1" | tar -xz -C "$WORK"
}
fetch "https://github.com/strukturag/libheif/archive/refs/tags/v$LIBHEIF_VERSION.tar.gz" "libheif-$LIBHEIF_VERSION"
fetch "https://github.com/strukturag/libde265/archive/refs/tags/v$LIBDE265_VERSION.tar.gz" "libde265-$LIBDE265_VERSION"
fetch "https://code.videolan.org/videolan/dav1d/-/archive/$DAV1D_VERSION/dav1d-$DAV1D_VERSION.tar.gz" "dav1d-$DAV1D_VERSION"

if [[ $(uname) == Darwin ]]; then
    lib=libheif.1.dylib
    link_flags=""
else
    lib=libheif.so.1
    export CC=${CC:-gcc-10} CXX=${CXX:-g++-10}
    # The C++ runtime goes inside (no dependency on the target's libstdc++); only the heif_* API is exported,
    # so neither it nor the static codecs can clash with another copy in the process
    printf '{ global: heif_*; local: *; };\n' > "$WORK/exports.map"
    link_flags="-static-libstdc++ -static-libgcc -Wl,--exclude-libs,ALL -Wl,--version-script=$WORK/exports.map"
fi

meson setup "$WORK/dav1d-build" "$WORK/dav1d-$DAV1D_VERSION" --wipe --buildtype=release --default-library=static \
    --prefix="$PREFIX" --libdir=lib -Denable_tools=false -Denable_tests=false -Denable_examples=false -Db_staticpic=true
ninja -C "$WORK/dav1d-build" install

cmake -S "$WORK/libde265-$LIBDE265_VERSION" -B "$WORK/de265-build" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$PREFIX" -DBUILD_SHARED_LIBS=OFF -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DENABLE_SDL=OFF -DENABLE_DECODER=OFF -DENABLE_ENCODER=OFF
cmake --build "$WORK/de265-build" -j "$jobs" --target install

# Decoding only, two codecs: everything else off so nothing else gets found and linked
off=()
for o in X265 X264 OpenH264_DECODER AOM_DECODER AOM_ENCODER SvtEnc RAV1E KVAZAAR JPEG_DECODER JPEG_ENCODER \
         OpenJPEG_DECODER OpenJPEG_ENCODER OPENJPH_ENCODER FFMPEG_DECODER LIBSHARPYUV UNCOMPRESSED_CODEC \
         EXAMPLES GDK_PIXBUF; do off+=("-DWITH_$o=OFF"); done
cmake -S "$WORK/libheif-$LIBHEIF_VERSION" -B "$WORK/heif-build" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$PREFIX" -DCMAKE_PREFIX_PATH="$PREFIX" -DBUILD_SHARED_LIBS=ON \
    -DBUILD_TESTING=OFF -DBUILD_DOCUMENTATION=OFF -DENABLE_PLUGIN_LOADING=OFF \
    -DWITH_LIBDE265=ON -DWITH_LIBDE265_PLUGIN=OFF -DWITH_DAV1D=ON -DWITH_DAV1D_PLUGIN=OFF \
    -DLIBDE265_INCLUDE_DIR="$PREFIX/include" -DLIBDE265_LIBRARY="$PREFIX/lib/libde265.a" \
    -DDAV1D_INCLUDE_DIR="$PREFIX/include" -DDAV1D_LIBRARY="$PREFIX/lib/libdav1d.a" \
    "${off[@]}" \
    -DCMAKE_SHARED_LINKER_FLAGS="$link_flags" -DCMAKE_CXX_STANDARD_LIBRARIES="-lpthread -ldl -lm"
cmake --build "$WORK/heif-build" -j "$jobs"

cp -L "$(find "$WORK/heif-build" -name "$lib" | head -1)" "$OUT/$lib"
if [[ $(uname) == Darwin ]]; then strip -x "$OUT/$lib"; else strip --strip-unneeded "$OUT/$lib"; fi
cp "$WORK/libheif-$LIBHEIF_VERSION/COPYING" "$OUT/licenses/libheif.txt"
cp "$WORK/libde265-$LIBDE265_VERSION/COPYING" "$OUT/licenses/libde265.txt"
cp "$WORK/dav1d-$DAV1D_VERSION/COPYING" "$OUT/licenses/dav1d.txt"
cat > "$OUT/licenses/README.txt" <<NOTICE
$(basename "$OUT/$lib") is libheif $LIBHEIF_VERSION (LGPL-3.0) with libde265 $LIBDE265_VERSION (LGPL-3.0) and
dav1d $DAV1D_VERSION (BSD-2-Clause) linked in, built unmodified by build/native in the KanTuNext repository.
You may replace it with your own build of the same libraries. Sources:
  https://github.com/strukturag/libheif/archive/refs/tags/v$LIBHEIF_VERSION.tar.gz
  https://github.com/strukturag/libde265/archive/refs/tags/v$LIBDE265_VERSION.tar.gz
  https://code.videolan.org/videolan/dav1d/-/archive/$DAV1D_VERSION/dav1d-$DAV1D_VERSION.tar.gz
NOTICE
ls -l "$OUT"
