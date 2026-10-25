Name: kantunext
Version: %_version
Release: 1
Summary: 看图 - offline image viewer and organizer
License: MIT
Requires: libX11.so.6()(%{__isa_bits}bit)
Requires: libSM.so.6()(%{__isa_bits}bit)
Requires: fontconfig

%define _build_id_links none
# xz payload: about 30% smaller than the gzip default
%define _binary_payload w9.xzdio
# The AOT binary is already stripped; brp-strip would touch the bundled native libraries
%define __os_install_post %{nil}

%description
Offline image viewer and organizer, a rewrite of the discontinued 看图.

%install
mkdir -p %{buildroot}/opt/KanTuNext %{buildroot}%{_bindir} %{buildroot}%{_datadir}/applications %{buildroot}%{_datadir}/icons/hicolor/256x256/apps
cp -r %{_kantu_src}/* %{buildroot}/opt/KanTuNext/
ln -sf /opt/KanTuNext/KanTuNext %{buildroot}%{_bindir}/kantunext
cp %{_kantu_res}/kantunext.desktop %{buildroot}%{_datadir}/applications/kantunext.desktop
cp %{_kantu_icon} %{buildroot}%{_datadir}/icons/hicolor/256x256/apps/kantunext.png

%files
/opt/KanTuNext/
%{_bindir}/kantunext
%{_datadir}/applications/kantunext.desktop
%{_datadir}/icons/hicolor/256x256/apps/kantunext.png
