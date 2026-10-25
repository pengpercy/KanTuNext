#!/usr/bin/env bash
# Builds an AppImage, a deb and an rpm from a Linux publish folder.
# env: VERSION, RUNTIME (linux-x64|linux-arm64); input: build/KanTuNext/ (the dotnet publish output)
set -euo pipefail
cd "$(dirname "$0")/.."

case "$RUNTIME" in
    linux-x64) deb_arch=amd64; appimage_arch=x86_64; rpm_arch=x86_64;;
    linux-arm64) deb_arch=arm64; appimage_arch=aarch64; rpm_arch=aarch64;;
    *) echo "Unknown runtime $RUNTIME"; exit 1;;
esac

rm -f KanTuNext/*.dbg KanTuNext/*.pdb
chmod +x KanTuNext/KanTuNext
# 256px full-bleed icon (the Windows/Linux variant; macOS uses its own padded master)
mkdir -p icons && cp ../src/KanTuNext.Desktop/Assets/kantunext.png icons/kantunext.png

# AppImage
APPIMAGETOOL=appimagetool
if [[ ! -x $APPIMAGETOOL ]]; then
    tool_arch=$( [[ $(uname -m) == aarch64 ]] && echo aarch64 || echo x86_64 )
    curl -fsSL -o $APPIMAGETOOL "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$tool_arch.AppImage"
    chmod +x $APPIMAGETOOL
fi
APPDIR=KanTuNext.AppDir
rm -rf $APPDIR
mkdir -p $APPDIR/opt $APPDIR/usr/share/applications $APPDIR/usr/share/metainfo
cp -a KanTuNext $APPDIR/opt/KanTuNext
sed 's#^Exec=.*#Exec=AppRun %F#' resources/linux/kantunext.desktop > $APPDIR/usr/share/applications/com.kantunext.app.desktop
cp $APPDIR/usr/share/applications/com.kantunext.app.desktop $APPDIR/
cp icons/kantunext.png $APPDIR/kantunext.png
cp resources/linux/kantunext.appdata.xml $APPDIR/usr/share/metainfo/com.kantunext.app.appdata.xml
ln -sf opt/KanTuNext/KanTuNext $APPDIR/AppRun
ARCH=$appimage_arch ./$APPIMAGETOOL --no-appstream $APPDIR "KanTuNext-$VERSION.linux.$appimage_arch.AppImage"

# deb (xz: about 30% smaller than gzip)
DEB=deb-root
rm -rf $DEB
mkdir -p $DEB/DEBIAN $DEB/opt $DEB/usr/bin $DEB/usr/share/applications $DEB/usr/share/icons/hicolor/256x256/apps
cp -a KanTuNext $DEB/opt/KanTuNext
ln -sf /opt/KanTuNext/KanTuNext $DEB/usr/bin/kantunext
cp resources/linux/kantunext.desktop $DEB/usr/share/applications/kantunext.desktop
cp icons/kantunext.png $DEB/usr/share/icons/hicolor/256x256/apps/kantunext.png
sed -e "s/^Version:.*/Version: $VERSION/" -e "s/^Architecture:.*/Architecture: $deb_arch/" \
    -e "s/^Installed-Size:.*/Installed-Size: $(du -sk $DEB | cut -f1)/" resources/linux/control > $DEB/DEBIAN/control
dpkg-deb -Zxz --root-owner-group --build $DEB "kantunext_${VERSION}-1_${deb_arch}.deb"

# rpm
RPMTOP="$(pwd)/rpm-top"
rm -rf "$RPMTOP"
rpmbuild -bb --target="$rpm_arch" resources/linux/kantunext.spec \
    --define "_topdir $RPMTOP" --define "_version $VERSION" \
    --define "_kantu_src $(pwd)/KanTuNext" --define "_kantu_res $(pwd)/resources/linux" \
    --define "_kantu_icon $(pwd)/icons/kantunext.png"
mv "$RPMTOP/RPMS/$rpm_arch/kantunext-$VERSION-1.$rpm_arch.rpm" ./

ls -la KanTuNext-*.AppImage kantunext_*.deb kantunext-*.rpm
