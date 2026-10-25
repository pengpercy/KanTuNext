#!/usr/bin/env bash
# Builds KanTuNext.app, a zip and a DMG from a macOS publish folder.
# env: VERSION, RUNTIME (osx-x64|osx-arm64), CODESIGN_IDENTITY (optional; ad-hoc seal without it)
# input: build/KanTuNext/ (the dotnet publish output)
set -euo pipefail
cd "$(dirname "$0")/.."

case "$RUNTIME" in
    osx-arm64) arch=arm64;;
    osx-x64) arch=x86_64;;
    *) echo "Unknown runtime $RUNTIME"; exit 1;;
esac

APP=KanTuNext.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R KanTuNext/ "$APP/Contents/MacOS/"
rm -rf KanTuNext
# Symbols are shipped separately (CI uploads them as their own artifact), never in the bundle
rm -rf "$APP"/Contents/MacOS/*.dSYM "$APP"/Contents/MacOS/*.dsym "$APP"/Contents/MacOS/*.pdb
# SkiaSharp, HarfBuzz and AvaloniaNative ship universal dylibs; keep only this package's slice (Skia: 14.5 → ~7 MB)
for lib in "$APP"/Contents/MacOS/*.dylib; do
    if [[ "$(lipo -archs "$lib")" == *" "* ]]; then
        lipo -thin "$arch" "$lib" -output "$lib.thin" && mv "$lib.thin" "$lib"
    fi
done

ICONSET=App.iconset
rm -rf "$ICONSET" && mkdir -p "$ICONSET"
src=resources/icon/kantunext-macos-1024.png
for s in 16 32 128 256 512; do
    sips -z $s $s "$src" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
    sips -z $((s*2)) $((s*2)) "$src" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/App.icns"
rm -rf "$ICONSET"
sed "s/KANTUNEXT_VERSION/$VERSION/g" resources/macos/Info.plist > "$APP/Contents/Info.plist"
find resources/macos -maxdepth 1 -type d -name "*.lproj" -exec cp -R {} "$APP/Contents/Resources/" \;

# Sign inside-out: every nested Mach-O first (deepest first), then the bundle. Without a Developer ID the
# bundle still gets an ad-hoc seal so resources are covered and the thinned dylibs carry a signature.
sign_args=(--force --sign "${CODESIGN_IDENTITY:--}")
if [[ -n "${CODESIGN_IDENTITY:-}" ]]; then
    sign_args+=(--options runtime --timestamp)
fi
while IFS= read -r file; do
    if [[ "$file" != "$APP/Contents/MacOS/KanTuNext" ]] && file -b "$file" | grep -q '^Mach-O'; then
        codesign "${sign_args[@]}" "$file"
    fi
done < <(find "$APP/Contents" -type f | awk '{ print gsub("/", "/"), $0 }' | sort -rn | cut -d' ' -f2-)
codesign "${sign_args[@]}" "$APP"
codesign --verify --deep --strict "$APP"

# ditto keeps symlinks, xattrs and resource forks, which zip -r flattens
ditto -c -k --sequesterRsrc --keepParent "$APP" "KanTuNext_$VERSION.$RUNTIME.zip"

DMG="KanTuNext_$VERSION.$RUNTIME.dmg"
rm -f "$DMG"
DMG_SOURCE="$(mktemp -d "${TMPDIR:-/tmp}/kantunext-dmg.XXXXXX")"
trap 'rm -rf "$DMG_SOURCE"' EXIT
cp -R "$APP" "$DMG_SOURCE/"
ln -s /Applications "$DMG_SOURCE/Applications"
# ULMO (LZMA) is about a third smaller than UDZO; needs macOS 10.15+, below .NET 10's own minimum.
# hdiutil fails intermittently with "Resource busy" on CI runners, hence the retries.
for attempt in 1 2 3; do
    if hdiutil create -volname "看图" -srcfolder "$DMG_SOURCE" -ov -format ULMO "$DMG"; then break; fi
    if [[ $attempt -eq 3 ]]; then echo "hdiutil create failed 3 times"; exit 1; fi
    echo "hdiutil create failed, retrying in 5 seconds ($attempt/3)..."; sleep 5
done
if [[ -n "${CODESIGN_IDENTITY:-}" ]]; then
    codesign --force --timestamp --sign "$CODESIGN_IDENTITY" "$DMG"
fi
echo "Packaged $RUNTIME: $(du -h "$DMG" | cut -f1) dmg, $(du -h "KanTuNext_$VERSION.$RUNTIME.zip" | cut -f1) zip"
