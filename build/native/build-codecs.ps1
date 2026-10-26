# Windows counterpart of build-codecs.sh: heif.dll (libheif with libde265 and dav1d linked in statically) plus
# the license texts. The C runtime is linked statically too, so the DLL needs no VC++ redistributable and the
# same build serves the Windows 7 package.
# usage: build-codecs.ps1 <out-dir>, from an MSVC developer shell with cmake, ninja, meson (and nasm on x64)
param([Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

Get-Content "$PSScriptRoot\versions.env" | Where-Object { $_ -match '^(\w+)=(.+)$' } | ForEach-Object { Set-Variable $Matches[1] $Matches[2] }
$work = if ($env:WORK) { $env:WORK } else { Join-Path $PSScriptRoot "..\..\.cache\codecs-build" }
New-Item -ItemType Directory -Force $work, (Join-Path $Out "licenses") | Out-Null
$work = (Resolve-Path $work).Path
$Out = (Resolve-Path $Out).Path
$prefix = "$work\prefix"

function Fetch([string]$url, [string]$dir) {
    if (Test-Path "$work\$dir") { return }
    Invoke-WebRequest $url -OutFile "$work\$dir.tar.gz"
    tar -xzf "$work\$dir.tar.gz" -C $work
}
Fetch "https://github.com/strukturag/libheif/archive/refs/tags/v$LIBHEIF_VERSION.tar.gz" "libheif-$LIBHEIF_VERSION"
Fetch "https://github.com/strukturag/libde265/archive/refs/tags/v$LIBDE265_VERSION.tar.gz" "libde265-$LIBDE265_VERSION"
Fetch "https://code.videolan.org/videolan/dav1d/-/archive/$DAV1D_VERSION/dav1d-$DAV1D_VERSION.tar.gz" "dav1d-$DAV1D_VERSION"

# arm64 assembly needs gas-preprocessor under MSVC; the C paths are fast enough for still images
$asm = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "false" } else { "true" }
meson setup "$work\dav1d-build" "$work\dav1d-$DAV1D_VERSION" --wipe --buildtype=release --default-library=static `
    "--prefix=$prefix" --libdir=lib -Db_vscrt=mt "-Denable_asm=$asm" `
    -Denable_tools=false -Denable_tests=false -Denable_examples=false
ninja -C "$work\dav1d-build" install

$crt = "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded", "-DCMAKE_POLICY_DEFAULT_CMP0091=NEW"
cmake -S "$work\libde265-$LIBDE265_VERSION" -B "$work\de265-build" -G Ninja -DCMAKE_BUILD_TYPE=Release @crt `
    "-DCMAKE_INSTALL_PREFIX=$prefix" -DBUILD_SHARED_LIBS=OFF -DENABLE_SDL=OFF -DENABLE_DECODER=OFF -DENABLE_ENCODER=OFF
cmake --build "$work\de265-build" --target install

$de265 = (Get-ChildItem "$prefix\lib" -Filter "*de265*.lib" | Select-Object -First 1).FullName
$dav1d = (Get-ChildItem "$prefix\lib" -Filter "libdav1d.*" | Select-Object -First 1).FullName
# Decoding only, two codecs: everything else off so nothing else gets found and linked
$off = "X265 X264 OpenH264_DECODER AOM_DECODER AOM_ENCODER SvtEnc RAV1E KVAZAAR JPEG_DECODER JPEG_ENCODER " +
       "OpenJPEG_DECODER OpenJPEG_ENCODER OPENJPH_ENCODER FFMPEG_DECODER LIBSHARPYUV UNCOMPRESSED_CODEC EXAMPLES GDK_PIXBUF"
$offArgs = $off.Split(" ") | ForEach-Object { "-DWITH_$_=OFF" }
cmake -S "$work\libheif-$LIBHEIF_VERSION" -B "$work\heif-build" -G Ninja -DCMAKE_BUILD_TYPE=Release @crt `
    "-DCMAKE_INSTALL_PREFIX=$prefix" "-DCMAKE_PREFIX_PATH=$prefix" -DBUILD_SHARED_LIBS=ON `
    -DBUILD_TESTING=OFF -DBUILD_DOCUMENTATION=OFF -DENABLE_PLUGIN_LOADING=OFF `
    -DWITH_LIBDE265=ON -DWITH_LIBDE265_PLUGIN=OFF -DWITH_DAV1D=ON -DWITH_DAV1D_PLUGIN=OFF `
    "-DLIBDE265_INCLUDE_DIR=$prefix\include" "-DLIBDE265_LIBRARY=$de265" `
    "-DDAV1D_INCLUDE_DIR=$prefix\include" "-DDAV1D_LIBRARY=$dav1d" `
    "-DCMAKE_CXX_FLAGS=/DWIN32 /D_WINDOWS /GR /EHsc /DLIBDE265_STATIC_BUILD" "-DCMAKE_C_FLAGS=/DWIN32 /D_WINDOWS /DLIBDE265_STATIC_BUILD" @offArgs
cmake --build "$work\heif-build"

Copy-Item (Get-ChildItem "$work\heif-build" -Recurse -Filter "heif.dll" | Select-Object -First 1).FullName "$Out\heif.dll"
Copy-Item "$work\libheif-$LIBHEIF_VERSION\COPYING" "$Out\licenses\libheif.txt"
Copy-Item "$work\libde265-$LIBDE265_VERSION\COPYING" "$Out\licenses\libde265.txt"
Copy-Item "$work\dav1d-$DAV1D_VERSION\COPYING" "$Out\licenses\dav1d.txt"
@"
heif.dll is libheif $LIBHEIF_VERSION (LGPL-3.0) with libde265 $LIBDE265_VERSION (LGPL-3.0) and
dav1d $DAV1D_VERSION (BSD-2-Clause) linked in, built unmodified by build/native in the KanTuNext repository.
You may replace it with your own build of the same libraries. Sources:
  https://github.com/strukturag/libheif/archive/refs/tags/v$LIBHEIF_VERSION.tar.gz
  https://github.com/strukturag/libde265/archive/refs/tags/v$LIBDE265_VERSION.tar.gz
  https://code.videolan.org/videolan/dav1d/-/archive/$DAV1D_VERSION/dav1d-$DAV1D_VERSION.tar.gz
"@ | Set-Content "$Out\licenses\README.txt"
dumpbin /dependents "$Out\heif.dll"
Get-ChildItem $Out -Recurse | Format-Table FullName, Length
